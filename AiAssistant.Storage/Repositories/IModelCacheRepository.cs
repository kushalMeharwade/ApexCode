using AiAssistant.Storage.Models;

namespace AiAssistant.Storage.Repositories;

public interface IModelCacheRepository
{
    Task<ModelCache?> GetByProviderAndModelIdAsync(string providerId, string modelId);
    Task<IEnumerable<ModelCache>> GetByProviderIdAsync(string providerId);
    Task<ModelCache> AddAsync(ModelCache modelCache);
    Task<IEnumerable<ModelCache>> GetAllAsync();
    Task DeleteExpiredAsync();
    Task RefreshFromApiAsync(string providerId, string apiKey);
}
