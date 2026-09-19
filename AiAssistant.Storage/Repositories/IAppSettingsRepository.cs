using System.Threading.Tasks;

namespace AiAssistant.Storage.Repositories;

public interface IAppSettingsRepository
{
    Task<string?> GetValueAsync(string key);
    Task SetValueAsync(string key, string value);
}
