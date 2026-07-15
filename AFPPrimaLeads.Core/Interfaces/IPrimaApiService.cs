using AFPPrimaLeads.Core.Entities;

namespace AFPPrimaLeads.Core.Interfaces
{
    public interface IPrimaApiService
    {
        Task<string> GetTokenAsync();
        Task InvalidateTokenAsync();
        Task<List<Prospecto>> GetProspectosAsync();
    }
}
