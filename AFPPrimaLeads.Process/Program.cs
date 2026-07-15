using AFPPrimaLeads.Core.Interfaces;
using AFPPrimaLeads.Infraestructure.Data;
using AFPPrimaLeads.Infraestructure.Monitoring;
using AFPPrimaLeads.Infraestructure.Repositories;
using AFPPrimaLeads.Infraestructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System.Diagnostics;

namespace AFPPrimaLeads.Process
{
    class Program
    {
        static async Task Main(string[] args)
        {
            string basePath = Debugger.IsAttached
               ? AppContext.BaseDirectory
               : @"C:\JobsDeployment\AFPPrimaLeads";

            // Cambio 9 del doc: un Windows Service no arranca con un CWD útil
            // (puede ser C:\Windows\System32). Fijarlo explícito antes de construir
            // configuración/logger.
            Environment.CurrentDirectory = basePath;

            Log.Logger = new LoggerConfiguration()
                .WriteTo.Console()
                .CreateBootstrapLogger();

            try
            {
                Log.Information("Iniciando AFPPrimaLeads Worker Service...");

                // ContentRootPath se fija ACÁ, antes de que el builder agregue sus fuentes
                // de configuración por default — así appsettings.json se carga desde
                // basePath directo, sin doble carga ni depender del CWD implícito.
                var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
                {
                    Args = args,
                    ContentRootPath = basePath
                });

                builder.Services.AddWindowsService(options => options.ServiceName = "AFPPrimaLeadsWorker");

                builder.Services.AddSerilog((services, loggerConfig) => loggerConfig
                    .ReadFrom.Configuration(builder.Configuration)
                    .Enrich.FromLogContext()
                    .Enrich.WithProperty("Application", "AFPPrimaLeads"));

                builder.Services.Configure<HostOptions>(o =>
                {
                    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
                    o.ShutdownTimeout = TimeSpan.FromSeconds(15);
                });

                var httpTimeoutSeconds = builder.Configuration.GetValue<int>("Http:TimeoutSeconds", 30);

                // Cambio 5 del doc: SocketsHttpHandler con PooledConnectionLifetime evita
                // seguir mandando tráfico a una conexión pooled apuntando a un backend
                // muerto si Prima/InConcert rotan de IP. ConfigureHttpClientDefaults (no
                // encadenado directo sobre cada AddHttpClient) aplica esto a todos los
                // clients registrados de una sola vez.
                builder.Services.ConfigureHttpClientDefaults(http =>
                {
                    http.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                    {
                        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
                    });
                    http.ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(httpTimeoutSeconds));
                });

                builder.Services.AddSingleton<DbContextApp>();
                builder.Services.AddTransient<IProspectoRepository, ProspectoRepository>();
                builder.Services.AddScoped<ILeadUploadService, LeadUploadService>();

                // PrimaApiService e InConcertApiService son Singleton a propósito: ambos
                // cachean su token de sesión (TTL ~1h) y el Circuit Breaker de InConcert
                // (Cambio 6b) necesita estado que persista ENTRE ticks del polling, no
                // solo entre workers concurrentes de un mismo tick.
                builder.Services.AddHttpClient(nameof(IPrimaApiService));
                builder.Services.AddSingleton<IPrimaApiService>(sp =>
                {
                    var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IPrimaApiService));
                    return new PrimaApiService(
                        httpClient,
                        sp.GetRequiredService<IConfiguration>(),
                        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PrimaApiService>>());
                });

                builder.Services.AddHttpClient(nameof(IInConcertApiService));
                builder.Services.AddSingleton<IInConcertApiService>(sp =>
                {
                    var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IInConcertApiService));
                    return new InConcertApiService(
                        httpClient,
                        sp.GetRequiredService<IConfiguration>(),
                        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<InConcertApiService>>());
                });

                builder.Services.AddKeyedSingleton<IHeartbeatMonitor, HeartbeatMonitor>("Producer");
                builder.Services.AddKeyedSingleton<IHeartbeatMonitor, HeartbeatMonitor>("Consumers");

                builder.Services.AddHostedService<Worker>();
                builder.Services.AddHostedService<WatchdogHostedService>();

                using var host = builder.Build();
                await host.RunAsync();

                Log.Information("Servicio detenido correctamente.");
            }
            catch (OperationCanceledException)
            {
                // Lección real del doc (Cambio 2): esto puede propagarse desde
                // WindowsServiceLifetime.StopAsync ante una señal externa (sc stop,
                // reinicio del server) — es un shutdown normal, no un crash.
                Log.Information("Servicio detenido por una señal externa (shutdown normal).");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Error fatal al iniciar o ejecutar el host.");
                Environment.ExitCode = 1;
            }
            finally
            {
                await Log.CloseAndFlushAsync();
            }
        }
    }
}
