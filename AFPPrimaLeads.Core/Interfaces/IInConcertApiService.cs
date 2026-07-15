using AFPPrimaLeads.Core.Entities;

namespace AFPPrimaLeads.Core.Interfaces
{
    public interface IInConcertApiService
    {
        Task<string> LoginAsync();
        Task InvalidateTokenAsync();
        Task<string?> AddContactAsync(OutboundRequest request);
        Task<bool> SetSkillsAsync(SetSkillsRequest request);
    }
}
