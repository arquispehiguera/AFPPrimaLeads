# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**AFPPrimaLeads** is a .NET 9 console application that fetches lead prospectos from the AFP Prima external REST API and uploads them to InConcert's outbound engine. It runs once per execution — Windows Task Scheduler handles recurrence.

## Build & Run Commands

```bash
# Restore dependencies
dotnet restore

# Build solution
dotnet build AFPPrimaLeads.sln

# Build for release
dotnet build -c Release

# Run the application
dotnet run --project AFPPrimaLeads.Process/AFPPrimaLeads.Process.csproj

# Publish
dotnet publish -c Release
```

No test project exists in this solution.

## Architecture

3-layer Clean Architecture split across 3 projects:

- **AFPPrimaLeads.Core** — Domain entities (`Lead`, `Prospecto`, `OutboundRequest`, `OutboundConfiguration`, `Address`, `Contact`, `ContactData`, `NameValue`) and interfaces (`ILeadUploadService`, `IPrimaApiService`, `IInConcertApiService`, `IProspectoRepository`). No external packages.
- **AFPPrimaLeads.Infraestructure** — All service and repository implementations, plus `DbContextApp`. Depends on Core.
- **AFPPrimaLeads.Process** — Console entry point (`Program.cs`). Builds a `ServiceCollection`, resolves `ILeadUploadService`, calls `UploadLeadsAsync()`, and exits with code `0` (success) or `1` (fatal error).

### Key Patterns

- **Orchestration**: `ILeadUploadService` → `LeadUploadService` — coordinates the full flow (see Data Flow below)
- **Prima API**: `IPrimaApiService` → `PrimaApiService` — OAuth2 token via client_credentials, then fetches prospectos from Prima's REST API
- **InConcert API**: `IInConcertApiService` → `InConcertApiService` — login + add_contacts with Polly retry (exponential backoff)
- **Repository**: `IProspectoRepository` → `ProspectoRepository` (Dapper, inserts into `GSS_Prospectos` and marks uploads)
- **DI**: plain `ServiceCollection` + `BuildServiceProvider()` — no `IHostBuilder` or `BackgroundService`

### Technology Stack

| Concern | Library |
|---|---|
| ORM | Dapper |
| Database | SQL Server (`Microsoft.Data.SqlClient`) |
| Logging | Serilog (MSSqlServer sink) |
| Serialization | Newtonsoft.Json |
| Resilience | Polly (retry with exponential backoff) |
| DI | Microsoft.Extensions.DependencyInjection |

## Data Flow

1. `PrimaApiService.GetTokenAsync()` — OAuth2 `POST /ux/oauth-manager-spa/token/v1/generation` with `client_id`/`client_secret` (form-encoded), header `Ocp-Apim-Subscription-Key`
2. `PrimaApiService.GetProspectosAsync(token)` — `GET /fc-gestionfonoprima/v1/prospectos` with `Authorization: Bearer {token}` and `Ocp-Apim-Subscription-Key`
   - **Note**: this call is currently commented out in `LeadUploadService`; when `ModoDemo: true` (config), a hardcoded test prospecto is used instead
3. `InConcertApiService.LoginAsync()` — `POST /login/` with `{ user, password }` → returns `token`
4. For each prospecto:
   - `ProspectoRepository.InsertAsync()` — inserts row into `GSS_Prospectos`, returns generated `Id`
   - Map `Prospecto` → `Lead` → `OutboundRequest`
   - `InConcertApiService.AddContactAsync(token, request)` — `POST /outbound_engine/batches/add_contacts/` with `Authorization: Bearer {token}`, retries on 5xx/timeout (Polly, up to `ResilienciaIC:RetryCount`)
   - On success: `ProspectoRepository.MarkUploadedAsync(gssId, elapsedSeconds, actionId)` — sets `ICUpload=1`, `ICTimeUpload`, `ContactId`

### OutboundRequest details

- `batchId` = `{ApiPrima:BatchId}{yyyyMM}` (e.g., `OutLeadsPrima_202603`)
- `campaignId` = `ApiPrima:CampaignId`
- Contact `id` = `{phone}@afpprima`
- `priority` = `10_000 + (int)DateTime.Now.TimeOfDay.TotalMinutes`
- `Address` fields (`Type`, `Kind`, `Channels`, `Number`) are PascalCase; all other `OutboundRequest` fields are camelCase — serialized as-is by Newtonsoft.Json (no custom attributes)
- `contactData.NameValuesSearchText` carries prospecto fields: `NOMBRE_COMPLETO`, `DNI`, `Correo`, `UltimoPaso`, `FechaUltimoPaso`, `Edad`, `FechaNacimiento`, `TipodeComision`, `AFPOrigen`, `EstuvoenPrima`, `TipodeCliente`, `DatosBCP`, `IndicadorPrima`, `ErrordeValidacionReniec`, `Genero`, `Canal`

## Configuration

`appsettings.json` lives in `AFPPrimaLeads.Infraestructure/` (copied to output dir at build):

- **ConnectionStrings:AppConnection** — SQL Server connection
- **ApiPrima** — Prima external API: `BaseUrl`, `SubscriptionKey`, `ProspectosSubscriptionKey`, `ClientId`, `ClientSecret`, `Scope`, `BatchId`, `CampaignId`
- **ApiKeyIC** — InConcert API: `BaseUrl`, `User`, `Password`
- **ResilienciaIC:RetryCount** — Polly retry count (default: 3)
- **ModoDemo** — `true` skips Prima API call and uses a hardcoded test prospecto
- **Serilog** — logs to SQL Server table `GSS_LogAFPrima`; enriched with `MachineName`, `Application`

Production deployment path: `C:\JobsDeployment\AFPPrimaLeads`

## Database

Table `dbo.GSS_Prospectos` — rows are inserted (not read) by this service. Key columns: `Dni`, `PrimerNombre`, `SegundoNombre`, `PrimerApellido`, `SegundoApellido`, `Genero`, `Email`, `Celular`, `UltimoPaso`, `FechaUltimoPaso`, `Canal`, `Edad`, `FechaNacimiento`, `TipoComision`, `AfpOrigen`, `IndicadorEnPrima`, `TipoCliente`, `CelularBcp`, `RamBcp`, `RamPrima`, `FechaAfiliacionPrima`, `ErrorValidacionReniec`, `ParametrosUtm`, `OutboundProcessID`, `BatchId`, `ICUpload`, `ICTimeUpload`, `ContactId`.
