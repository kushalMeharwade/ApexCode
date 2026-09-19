using System.Linq;
using AiAssistant.Core.Services;
using AiAssistant.Storage.Models;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;

namespace ApexCode.Services;

/// <summary>
/// Manages the active persona state and injection pipeline.
/// Lives in VSIX to access the full DI container.
/// </summary>
public class PersonaService : IPersonaService
{
    private readonly ISystemPromptManager _promptManager;
    private readonly IDatabaseConnectionService? _dbService;
    private SystemPrompt? _activePersona;

    public SystemPrompt? ActivePersona => _activePersona;

    public PersonaService(ISystemPromptManager promptManager, IDatabaseConnectionService? dbService = null)
    {
        _promptManager = promptManager ?? throw new ArgumentNullException(nameof(promptManager));
        _dbService = dbService;
    }

    public async Task<IReadOnlyList<SystemPrompt>> GetAllPersonasAsync()
    {
        return (await _promptManager.GetAllPromptsAsync()).ToList();
    }

    public async Task SetActivePersonaAsync(string personaId)
    {
        var persona = await _promptManager.GetPromptAsync(personaId);
        if (persona != null)
        {
            _activePersona = persona;
        }
        else
        {
            // Fallback to default
            _activePersona = await _promptManager.GetDefaultPromptAsync();
        }
    }

    public void ClearActivePersona()
    {
        _activePersona = null;
    }

    /// <summary>
    /// Gets the wrapped system prompt for LLM injection.
    /// Format: [SYSTEM PERSONA]\n{Description}\n[/SYSTEM PERSONA]
    /// Falls back to a baseline default if no persona is active.
    /// </summary>
    public async Task<string> GetInjectedPersonaAsync()
    {
        var persona = _activePersona ?? await _promptManager.GetDefaultPromptAsync();
        string content = persona?.Content ?? "You are a helpful AI coding assistant integrated into Visual Studio. You help developers write, review, and debug code. Always explain your changes clearly and prioritize readability over cleverness.";

        string dbContext = "";
        if (_dbService?.ActiveConnection != null)
        {
            var conn = _dbService.ActiveConnection;
            dbContext = $"\n\n[DATABASE CONTEXT]\nThe user has an active database connection configured named '{conn.Name}'. " +
                        $"The database engine is SQL Server; authentication is {conn.Authentication}. " +
                        $"You may inspect its schema and run validated read-only SELECT queries in both Plan and Act modes when relevant to the user's request. Writes and procedures are blocked in both modes. " +
                        $"Use this active connection when invoking database-related tools.\n[/DATABASE CONTEXT]";
        }

        return $"[SYSTEM PERSONA]\n{content}\n[/SYSTEM PERSONA]{dbContext}";
    }

    public async Task<SystemPrompt> CreatePersonaAsync(string name, string description, bool isDefault = false)
    {
        return await _promptManager.CreatePromptAsync(name, description, isDefault);
    }

    public async Task<SystemPrompt> UpdatePersonaAsync(SystemPrompt persona)
    {
        return await _promptManager.UpdatePromptAsync(persona);
    }

    public async Task DeletePersonaAsync(string personaId)
    {
        // If deleting the active persona, clear it (will fallback to default)
        if (_activePersona?.Id == personaId)
        {
            _activePersona = null;
        }

        await _promptManager.DeletePromptAsync(personaId);
    }

    public async Task SetDefaultPersonaAsync(string personaId)
    {
        await _promptManager.SetDefaultPromptAsync(personaId);
    }
}
