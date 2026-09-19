using AiAssistant.Storage.Models;

namespace AiAssistant.Storage.Repositories;

public interface ISystemPromptRepository
{
    Task<SystemPrompt?> GetByIdAsync(string id);
    Task<IEnumerable<SystemPrompt>> GetAllAsync();
    Task<SystemPrompt?> GetByNameAsync(string name);
    Task<SystemPrompt?> GetDefaultAsync();
    Task<SystemPrompt> AddAsync(SystemPrompt prompt);
    Task<SystemPrompt> UpdateAsync(SystemPrompt prompt);
    Task DeleteAsync(string id);
    Task SetDefaultAsync(string id);
}
