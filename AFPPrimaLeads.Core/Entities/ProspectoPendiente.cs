namespace AFPPrimaLeads.Core.Entities
{
    public class ProspectoPendiente
    {
        public int Id { get; set; }
        public string ContactId { get; set; } = string.Empty;
        public string BatchId { get; set; } = string.Empty;
        public Prospecto Prospecto { get; set; } = new();
    }
}
