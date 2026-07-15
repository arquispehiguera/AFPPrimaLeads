using AFPPrimaLeads.Core.Entities;

namespace AFPPrimaLeads.Core.Interfaces
{
    public interface IProspectoRepository
    {
        Task<int> InsertAsync(Prospecto prospecto, string batchId, string outboundProcessId, string contactId, CancellationToken ct = default);
        Task<IReadOnlyList<ProspectoPendiente>> GetPendingRetryAsync(int maxBatchSize, CancellationToken ct = default);
        Task MarkUploadedAsync(int id, int elapsedSeconds, string contactId, CancellationToken ct = default);
        Task RegisterFailedAttemptAsync(int id, CancellationToken ct = default);
    }
}
