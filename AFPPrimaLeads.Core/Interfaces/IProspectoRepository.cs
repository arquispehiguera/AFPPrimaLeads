using AFPPrimaLeads.Core.Entities;

namespace AFPPrimaLeads.Core.Interfaces
{
    public interface IProspectoRepository
    {
        Task<int> InsertAsync(Prospecto prospecto, string batchId, string outboundProcessId, string contactId);
        Task<IReadOnlyList<ProspectoPendiente>> GetPendingRetryAsync(int maxBatchSize);
        Task MarkUploadedAsync(int id, int elapsedSeconds, string contactId);
        Task RegisterFailedAttemptAsync(int id);
    }
}
