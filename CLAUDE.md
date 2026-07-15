# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**AFPPrimaLeads** is a .NET 9 **Windows Service** that continuously polls the AFP Prima external REST API for lead prospectos and uploads them to InConcert's outbound engine. It is long-running (not one-shot): a background worker ticks on a fixed interval, and a watchdog restarts the process if it hangs or stops making progress. See `ARQUITECTURA-WINDOWS-SERVICE.md` and `DEPLOY-WORKER-SERVICE.md` for the full migration history and deployment steps.

## Build & Run Commands

```bash
# Restore dependencies
dotnet restore

# Build solution
dotnet build AFPPrimaLeads.sln

# Build for release
dotnet build -c Release

# Run interactively (console mode, not installed as a service)
dotnet run --project AFPPrimaLeads.Process/AFPPrimaLeads.Process.csproj

# Publish
dotnet publish -c Release
```

No test project exists in this solution.

## Architecture

3-layer Clean Architecture split across 3 projects. Layering is respected: Core has no dependencies, Infraestructure depends only on Core, Process depends on both.

- **AFPPrimaLeads.Core** — Domain entities and interfaces, no external packages.
  - Entities: `Lead`, `Prospecto` (now includes `utmSource/Medium/Campaign/Content` and `jsonClient`), `ProspectoPendiente` (a pending-retry record), `OutboundRequest`/`OutboundConfiguration`/`Address`/`Contact`/`ContactData`/`NameValue` (in `OutboundModels.cs`), plus `SetSkillsRequest`/`SkillItem` for InConcert's skills endpoint.
  - Interfaces: `ILeadUploadService`, `IPrimaApiService`, `IInConcertApiService`, `IProspectoRepository`, `IHeartbeatMonitor`.
- **AFPPrimaLeads.Infraestructure** — All service and repository implementations, plus `DbContextApp` and `HeartbeatMonitor`.
- **AFPPrimaLeads.Process** — Host entry point (`Program.cs`). Builds a `Host` via `Host.CreateApplicationBuilder`, registers it as a Windows Service (`AddWindowsService`), wires DI, and runs two hosted `BackgroundService`s (`Worker`, `WatchdogHostedService`) until shutdown.

### Key Patterns

- **Host & lifecycle**: `Program.cs` pins `Environment.CurrentDirectory` and `ContentRootPath` to `C:\JobsDeployment\AFPPrimaLeads` before the builder runs, since a Windows Service doesn't start with a useful CWD. `OperationCanceledException` at the top level is treated as a normal shutdown (service stop / server restart), not a crash.
- **Worker** (`Worker : BackgroundService`) — polls on `Worker:PollingIntervalSeconds` via `PeriodicTimer`, resolves `ILeadUploadService` from a fresh DI scope per tick, and guards against overlapping runs with a `SemaphoreSlim` (structural, since the loop is sequential/awaited — the semaphore is defense against a future non-sequential trigger).
- **Watchdog** (`WatchdogHostedService : BackgroundService`) — polls two `IHeartbeatMonitor` instances (keyed `"Producer"`/`"Consumers"`) on `Watchdog:CheckIntervalSeconds`. If either heartbeat goes stale (`Watchdog:StaleThresholdSeconds`) or progress stalls (`Watchdog:MaxNoProgressMinutes`), it logs critical and calls `IHostApplicationLifetime.StopApplication()` (graceful shutdown, deliberately **not** `Environment.FailFast`) so the SCM restarts the service.
- **Orchestration**: `ILeadUploadService` → `LeadUploadService` — producer/consumer pipeline over a bounded `Channel<UploadItem>` (see Data Flow below).
- **Prima API**: `IPrimaApiService` → `PrimaApiService` — OAuth2 token via client_credentials (cached, double-checked locking via `SemaphoreSlim` with timeout), `InvalidateTokenAsync()` for forced refresh on 401, then fetches prospectos.
- **InConcert API**: `IInConcertApiService` → `InConcertApiService` — login + `AddContactAsync` + `SetSkillsAsync`, each with its own token handling; Polly retry wrapped in a circuit breaker (`Policy.Wrap(Retry, CircuitBreaker)`). Login/add_contacts and set_skills use **separate** circuit breaker policies so a skills-endpoint blip can't block contact uploads.
- **Repository**: `IProspectoRepository` → `ProspectoRepository` (Dapper) — `InsertAsync`, `GetPendingRetryAsync(maxBatchSize)` (bounds the retry backlog per run via `TOP(@MaxBatchSize)`), `MarkUploadedAsync`, `RegisterFailedAttemptAsync`. Has its own Polly retry for transient SQL errors (timeout/deadlock/connection) — a layer that previously only existed for HTTP calls.
- **DI**: `PrimaApiService` and `InConcertApiService` are registered as **Singleton on purpose** — both cache a session token (TTL config, default ~50 min) and InConcert's circuit breaker needs state that persists across polling ticks, not just across concurrent workers within one tick. `HeartbeatMonitor` is registered twice as `AddKeyedSingleton` (`"Producer"`, `"Consumers"`), thread-safe via `Interlocked`.

### Technology Stack

| Concern | Library |
|---|---|
| Host | `Microsoft.Extensions.Hosting` (`Host.CreateApplicationBuilder`) + `Microsoft.Extensions.Hosting.WindowsServices` |
| ORM | Dapper |
| Database | SQL Server (`Microsoft.Data.SqlClient`) |
| Logging | Serilog (MSSqlServer + File sinks) |
| Serialization | Newtonsoft.Json |
| Resilience | Polly (retry + circuit breaker, HTTP and SQL) |
| DI | Microsoft.Extensions.DependencyInjection |

## Data Flow

`LeadUploadService.UploadLeadsAsync()` runs once per `Worker` tick, as a producer/consumer pipeline:

1. A bounded `Channel<UploadItem>` (`InConcert:ChannelCapacity`) is created; `InConcert:MaxParallelUploads` consumer tasks start reading from it concurrently.
2. **Producer**: first drains `ProspectoRepository.GetPendingRetryAsync(maxRetryBatchSize)` (prior failed uploads, retried first) into the channel, then calls `PrimaApiService.GetProspectosAsync()` (OAuth2 token handled internally) and, for each new prospecto, `ProspectoRepository.InsertAsync()` (assigns a `GssId` + generated `ContactId`) before writing it to the channel. Reports `IHeartbeatMonitor.ReportAlive()`/`ReportProgress()` (Producer) as it goes.
3. **Consumers**: each reads items and calls `ProcessItemAsync`:
   - Computes `priority` (see below), maps `Prospecto` → `Lead` → `OutboundRequest`.
   - `InConcertApiService.AddContactAsync(request)` — `POST /outbound_engine/batches/add_contacts/`, retried under the login/add_contacts circuit breaker.
   - On success: `ProspectoRepository.MarkUploadedAsync(gssId, elapsedSeconds, contactId)`; if `SkillsIC:Enabled`, also calls `SetSkillsAsync` (failure here only logs a warning, doesn't fail the contact).
   - On failure/exception: `ProspectoRepository.RegisterFailedAttemptAsync(gssId)` — bumps the retry counter until `ReintentosGss:MaxIntentos` is hit.
   - Reports `IHeartbeatMonitor` (Consumers) per item; also reports "alive" (not "progress") when idle 15s or when the channel closes empty, so quiet ticks don't falsely trip the Watchdog.
4. Each token is resolved per-call inside the API services (cached + self-healing on 401) — no single token is captured once at the start of a run.

### OutboundRequest details

- `batchId` = `{ApiPrima:BatchId}{yyyyMM}`, recomputed every run (not cached in the constructor, since the service now lives inside a long-running host)
- `campaignId` = `ApiPrima:CampaignId`
- Contact `id` = `{contactId}@afpprima` (contactId is now a generated GUID, not the phone number)
- `priority` — base `10_000` in peak hours (08:00–19:00 America/Lima) or `8_000` off-peak, plus minutes-of-day; if `prospecto.canal == "AMP"`, a +10% (off-peak) or +5% (peak) bonus is applied
- `Address` fields (`Type`, `Kind`, `Channels`, `Number`) are PascalCase; all other `OutboundRequest` fields are camelCase — serialized as-is by Newtonsoft.Json (no custom attributes)
- `contactData.NameValuesSearchText` carries prospecto fields: `NOMBRE_COMPLETO`, `DNI`, `Correo`, `UltimoPaso`, `FechaUltimoPaso`, `Edad`, `FechaNacimiento`, `TipodeComision`, `AFPOrigen`, `EstuvoenPrima`, `TipodeCliente`, `DatosBCP`, `IndicadorPrima`, `ErrordeValidacionReniec`, `Genero`, `Canal`, `UtmSource`, `UtmMedium`, `UtmCampaign`, `UtmContent`

## Configuration

`appsettings.json` lives in `AFPPrimaLeads.Process/` (not Infraestructure) and is **gitignored** — real secrets stay local/on the deploy target only.

- **ConnectionStrings:AppConnection** — SQL Server connection
- **ApiPrima** — Prima external API: `BaseUrl`, `SubscriptionKey`, `ProspectosSubscriptionKey`, `ClientId`, `ClientSecret`, `Scope`, `BatchId`, `CampaignId`, `TokenLifetimeMinutes` (fallback TTL if OAuth response has no `expires_in`)
- **ApiKeyIC** — InConcert API: `BaseUrl`, `User`, `Password`, `TokenLifetimeMinutes`
- **Resiliencia:Http:{InConcert,Prima}:RetryCount** — Polly HTTP retry counts (per-API)
- **Resiliencia:Db:RetryCount** — Polly retry count for transient SQL errors
- **ReintentosGss:MaxIntentos** — max failed upload attempts per prospecto before it's abandoned (`ICUpload = 3`)
- **InConcert:MaxParallelUploads / ChannelCapacity / CircuitBreakerThreshold / MaxRetryBatchSize** — producer/consumer pipeline tuning
- **Http:TimeoutSeconds** — shared `HttpClient` timeout (default 100s is too high; also feeds the Watchdog's stale-threshold math)
- **Worker:PollingIntervalSeconds** — tick interval for the main worker
- **Watchdog:CheckIntervalSeconds / StaleThresholdSeconds / MaxNoProgressMinutes** — hang detection; `StaleThresholdSeconds` must be recalculated by hand if `RetryCount`, `Http:TimeoutSeconds`, or Polly backoff changes (see comments in `appsettings.json`)
- **SkillsIC:Enabled / Mix / Skills[]** — optional InConcert skill assignment after a successful contact upload
- **Serilog** — logs to SQL Server table `GSS_LogAFPrima` and a rolling file (`logs\afpprimaleads-*.log`); enriched with `MachineName`, `Application`

Production deployment path: `C:\JobsDeployment\AFPPrimaLeads`

## Database

Table `dbo.GSS_Prospectos` — rows are inserted (not read, except for pending retries) by this service. Key columns: `Dni`, `PrimerNombre`, `SegundoNombre`, `PrimerApellido`, `SegundoApellido`, `Genero`, `Email`, `Celular`, `UltimoPaso`, `FechaUltimoPaso`, `Canal`, `Edad`, `FechaNacimiento`, `TipoComision`, `AfpOrigen`, `IndicadorEnPrima`, `TipoCliente`, `CelularBcp`, `RamBcp`, `RamPrima`, `FechaAfiliacionPrima`, `ErrorValidacionReniec`, `ParametrosUtm`, `OutboundProcessID`, `BatchId`, `ICUpload`, `ICTimeUpload`, `ContactId`, `IntentosIC` (retry counter — added by migration `sql/2026-07-14_add-intentos-ic.sql`; check `DEPLOY-WORKER-SERVICE.md` for whether it has run against the target database yet).
