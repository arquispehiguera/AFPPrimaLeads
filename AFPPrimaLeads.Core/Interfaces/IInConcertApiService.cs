using AFPPrimaLeads.Core.Entities;

namespace AFPPrimaLeads.Core.Interfaces
{
    public interface IInConcertApiService
    {
        Task<string> LoginAsync(CancellationToken ct = default);
        Task InvalidateTokenAsync();
        Task<ContactUploadResult> AddContactAsync(OutboundRequest request, CancellationToken ct = default);
        Task<bool> SetSkillsAsync(SetSkillsRequest request, CancellationToken ct = default);
    }
}
