using AFPPrimaLeads.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AFPPrimaLeads.Process
{
    public class WatchdogHostedService : BackgroundService
    {
        private readonly IHeartbeatMonitor _producerHeartbeat;
        private readonly IHeartbeatMonitor _consumersHeartbeat;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly ILogger<WatchdogHostedService> _logger;
        private readonly TimeSpan _checkInterval;
        private readonly TimeSpan _staleThreshold;
        private readonly TimeSpan _maxNoProgress;

        public WatchdogHostedService(
            [FromKeyedServices("Producer")] IHeartbeatMonitor producerHeartbeat,
            [FromKeyedServices("Consumers")] IHeartbeatMonitor consumersHeartbeat,
            IHostApplicationLifetime lifetime,
            ILogger<WatchdogHostedService> logger,
            IConfiguration configuration)
        {
            _producerHeartbeat = producerHeartbeat;
            _consumersHeartbeat = consumersHeartbeat;
            _lifetime = lifetime;
            _logger = logger;
            _checkInterval = TimeSpan.FromSeconds(configuration.GetValue<int>("Watchdog:CheckIntervalSeconds", 30));
            _staleThreshold = TimeSpan.FromSeconds(configuration.GetValue<int>("Watchdog:StaleThresholdSeconds", 240));
            _maxNoProgress = TimeSpan.FromMinutes(configuration.GetValue<int>("Watchdog:MaxNoProgressMinutes", 15));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "Watchdog iniciado. StaleThreshold={StaleThreshold}, MaxNoProgress={MaxNoProgress}.",
                _staleThreshold, _maxNoProgress);

            using var timer = new PeriodicTimer(_checkInterval);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = DateTime.UtcNow;
                var elapsedProducer = now - _producerHeartbeat.LastHeartbeatUtc;
                var elapsedConsumers = now - _consumersHeartbeat.LastHeartbeatUtc;
                var elapsedProducerProgress = now - _producerHeartbeat.LastProgressUtc;
                var elapsedConsumersProgress = now - _consumersHeartbeat.LastProgressUtc;

                var heartbeatVencido = elapsedProducer > _staleThreshold || elapsedConsumers > _staleThreshold;
                var sinProgresoReal = elapsedProducerProgress > _maxNoProgress || elapsedConsumersProgress > _maxNoProgress;

                if (heartbeatVencido || sinProgresoReal)
                {
                    _logger.LogCritical(
                        "Watchdog detectó un problema — Producer alive hace {ElapsedProducer}, Consumers alive hace {ElapsedConsumers}, " +
                        "Producer sin progreso hace {ElapsedProducerProgress}, Consumers sin progreso hace {ElapsedConsumersProgress}. " +
                        "Forzando shutdown grácil para que el SCM reinicie el servicio.",
                        elapsedProducer, elapsedConsumers, elapsedProducerProgress, elapsedConsumersProgress);

                    // Sin FailFast a propósito (ver ARQUITECTURA-WINDOWS-SERVICE.md, Cambio 4b):
                    // esto pide un shutdown grácil acotado por HostOptions.ShutdownTimeout y deja
                    // que Log.CloseAndFlush() corra una sola vez al final de Main, sin competir con
                    // otros BackgroundService que sigan logueando.
                    Environment.ExitCode = 1;
                    _lifetime.StopApplication();
                    return;
                }
            }
        }
    }
}
