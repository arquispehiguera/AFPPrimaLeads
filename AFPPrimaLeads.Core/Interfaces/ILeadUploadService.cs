namespace AFPPrimaLeads.Core.Interfaces
{
    public interface ILeadUploadService
    {
        Task UploadLeadsAsync(CancellationToken ct = default);
    }
}
