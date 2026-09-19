using AiAssistant.Storage.Models;
using AiAssistant.Storage.Repositories;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Core.Services;

public class SystemPromptManager : ISystemPromptManager
{
    private readonly ISystemPromptRepository _promptRepository;
    private readonly ILogger<SystemPromptManager> _logger;

    public SystemPromptManager(ISystemPromptRepository promptRepository, ILogger<SystemPromptManager> logger)
    {
        _promptRepository = promptRepository;
        _logger = logger;
    }

    public async Task<SystemPrompt> CreatePromptAsync(string name, string content, bool isDefault = false)
    {
        var prompt = new SystemPrompt
        {
            Name = name,
            Content = content,
            IsDefault = isDefault
        };
        return await _promptRepository.AddAsync(prompt);
    }

    public async Task<SystemPrompt?> GetPromptAsync(string id)
    {
        return await _promptRepository.GetByIdAsync(id);
    }

    public async Task<SystemPrompt?> GetDefaultPromptAsync()
    {
        return await _promptRepository.GetDefaultAsync();
    }

    public async Task<IEnumerable<SystemPrompt>> GetAllPromptsAsync()
    {
        return await _promptRepository.GetAllAsync();
    }

    public async Task<SystemPrompt> UpdatePromptAsync(SystemPrompt prompt)
    {
        return await _promptRepository.UpdateAsync(prompt);
    }

    public async Task DeletePromptAsync(string id)
    {
        await _promptRepository.DeleteAsync(id);
    }

    public async Task SetDefaultPromptAsync(string id)
    {
        await _promptRepository.SetDefaultAsync(id);
    }

    public async Task<string> GetInterpolatedPromptAsync(string promptId, IDictionary<string, string>? variables = null)
    {
        SystemPrompt? prompt = null;
        if (string.IsNullOrWhiteSpace(promptId))
        {
            prompt = await _promptRepository.GetDefaultAsync();
            if (prompt == null)
            {
                prompt = await CreatePromptAsync("Default Assistant", "You are ApexCode, an intelligent coding assistant.", true);
            }
        }
        else
        {
            prompt = await _promptRepository.GetByIdAsync(promptId);
            if (prompt == null)
                throw new ArgumentException($"Prompt with id {promptId} not found", nameof(promptId));
        }

        var content = prompt.Content;
        if (variables != null)
        {
            foreach (var kvp in variables)
                content = content.Replace($"{{{{{kvp.Key}}}}}", kvp.Value);
        }
        return content;
    }
}
