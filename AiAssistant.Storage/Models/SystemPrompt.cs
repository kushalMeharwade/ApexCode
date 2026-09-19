using System;

namespace AiAssistant.Storage.Models;

public record SystemPrompt
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Name { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string? Variables { get; init; }
    public bool IsDefault { get; init; } = false;
    public string? IconResourceKey { get; init; }
    public bool IsBuiltIn { get; init; } = false;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}
