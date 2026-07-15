namespace AFPPrimaLeads.Core.Entities
{
    public class Lead
    {
        public string ContactId { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string BatchId { get; set; } = string.Empty;
        public string CampaignId { get; set; } = string.Empty;
        public int Priority { get; set; } = 10_000;
    }
}
