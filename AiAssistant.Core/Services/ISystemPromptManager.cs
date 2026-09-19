using AiAssistant.Storage.Models;

namespace AiAssistant.Core.Services;

public interface ISystemPromptManager
{
    Task<SystemPrompt> CreatePromptAsync(string name, string content, bool isDefault = false);
    Task<SystemPrompt?> GetPromptAsync(string id);
    Task<SystemPrompt?> GetDefaultPromptAsync();
    Task<IEnumerable<SystemPrompt>> GetAllPromptsAsync();
    Task<SystemPrompt> UpdatePromptAsync(SystemPrompt prompt);
    Task DeletePromptAsync(string id);
    Task SetDefaultPromptAsync(string id);
    Task<string> GetInterpolatedPromptAsync(string promptId, IDictionary<string, string>? variables = null);
}
