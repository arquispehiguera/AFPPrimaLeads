namespace AFPPrimaLeads.Core.Interfaces
{
    public interface IHeartbeatMonitor
    {
        void ReportAlive();
        DateTime LastHeartbeatUtc { get; }

        // Separado de "alive" a propósito: un worker puede estar sano y sin
        // trabajo disponible (ReportAlive se refresca) sin haber logrado nada
        // real todavía (ReportProgress no se refresca).
        void ReportProgress();
        DateTime LastProgressUtc { get; }
    }
}
