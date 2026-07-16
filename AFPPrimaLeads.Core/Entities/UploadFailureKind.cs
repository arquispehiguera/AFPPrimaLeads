namespace AFPPrimaLeads.Core.Entities
{
    public enum UploadFailureKind
    {
        // Infraestructura caída (circuito abierto, 5xx, timeout) — no cuenta como
        // intento real, el prospecto sigue elegible para reintento indefinidamente.
        Transient,

        // InConcert rechazó el contacto (4xx explícito) o la respuesta no tiene
        // forma válida — cuenta para IntentosIC.
        Permanent
    }
}
