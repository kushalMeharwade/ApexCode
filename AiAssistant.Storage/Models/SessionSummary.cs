namespace AiAssistant.Storage.Models;

/// <summary>History card data without conversation messages or serialized content blocks.</summary>
public sealed class SessionSummary
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
    public string? FirstPrompt { get; set; }
}
