using AFPPrimaLeads.Core.Entities;

namespace AFPPrimaLeads.Core.Interfaces
{
    public interface IPrimaApiService
    {
        Task<string> GetTokenAsync(CancellationToken ct = default);
        Task InvalidateTokenAsync();
        Task<List<Prospecto>> GetProspectosAsync(CancellationToken ct = default);
    }
}
