using System;

namespace AiAssistant.Storage.Models;

public record Message
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string SessionId { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string? TokenCount { get; init; }
    public string? ResponseTime { get; init; }
    public bool IsStreaming { get; init; } = false;
    public string? SerializedContentBlocks { get; init; }
    public string? OriginalPrompt { get; init; }
    public string? Metadata { get; init; }
}
