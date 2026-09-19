using AiAssistant.Storage.Models;

namespace AiAssistant.Core.Services;

/// <summary>
/// Manages the active persona (system prompt) state for the current chat session.
/// Handles persona selection, fallback to default, and injection wrapping.
/// </summary>
public interface IPersonaService
{
    /// <summary>
    /// The currently active persona, or null if none selected.
    /// </summary>
    SystemPrompt? ActivePersona { get; }

    /// <summary>
    /// All available personas.
    /// </summary>
    Task<IReadOnlyList<SystemPrompt>> GetAllPersonasAsync();

    /// <summary>
    /// Set the active persona by ID. Falls back to default if not found.
    /// </summary>
    Task SetActivePersonaAsync(string personaId);

    /// <summary>
    /// Clear the active persona (will use default on next injection).
    /// </summary>
    void ClearActivePersona();

    /// <summary>
    /// Get the wrapped system prompt text for injection into the LLM.
    /// Returns the [SYSTEM PERSONA] wrapped text, or a baseline default.
    /// </summary>
    Task<string> GetInjectedPersonaAsync();

    /// <summary>
    /// Create a new persona.
    /// </summary>
    Task<SystemPrompt> CreatePersonaAsync(string name, string description, bool isDefault = false);

    /// <summary>
    /// Update an existing persona.
    /// </summary>
    Task<SystemPrompt> UpdatePersonaAsync(SystemPrompt persona);

    /// <summary>
    /// Delete a persona. If it was the active one, falls back to default.
    /// </summary>
    Task DeletePersonaAsync(string personaId);

    /// <summary>
    /// Set a persona as the default.
    /// </summary>
    Task SetDefaultPersonaAsync(string personaId);
}
