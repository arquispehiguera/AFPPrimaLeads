using AFPPrimaLeads.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AFPPrimaLeads.Process
{
    public class Worker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<Worker> _logger;
        private readonly TimeSpan _pollingInterval;

        // Defensa extra, no la protección principal: el loop de abajo (secuencial,
        // awaited) ya es estructuralmente no-solapable. Esto cubre el caso de que
        // en el futuro alguien agregue un trigger manual o algo fire-and-forget.
        private readonly SemaphoreSlim _reentrancyGuard = new(1, 1);

        public Worker(IServiceScopeFactory scopeFactory, ILogger<Worker> logger, IConfiguration configuration)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            var seconds = configuration.GetValue<int>("Worker:PollingIntervalSeconds", 60);
            _pollingInterval = TimeSpan.FromSeconds(seconds);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(_pollingInterval);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!await _reentrancyGuard.WaitAsync(0, stoppingToken))
                {
                    _logger.LogWarning("La corrida anterior todavía sigue en curso — se descarta este tick.");
                    continue;
                }

                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var leadUploadService = scope.ServiceProvider.GetRequiredService<ILeadUploadService>();
                    await leadUploadService.UploadLeadsAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Shutdown normal (sc stop, reinicio del server) — no es un error real.
                }
                catch (Exception ex)
                {
                    // Una corrida fallida (dependencia externa caída, etc.) no debe tirar
                    // abajo todo el servicio — se loguea y se reintenta en el próximo tick.
                    // El Watchdog es quien decide si esto escaló a un hang real.
                    _logger.LogError(ex, "Error no controlado en la corrida de subida de leads.");
                }
                finally
                {
                    _reentrancyGuard.Release();
                }
            }
        }
    }
}
