using AiAssistant.Storage.Models;

namespace AiAssistant.Storage.Repositories;

public interface IProviderProfileRepository
{
    Task<ProviderProfile?> GetByIdAsync(string id);
    Task<IEnumerable<ProviderProfile>> GetAllAsync();
    Task<ProviderProfile?> GetByNameAsync(string name);
    Task<ProviderProfile> AddAsync(ProviderProfile profile);
    Task<ProviderProfile> UpdateAsync(ProviderProfile profile);
    Task DeleteAsync(string id);
}
