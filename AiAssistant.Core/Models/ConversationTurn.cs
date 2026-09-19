using System;
using System.Collections.Generic;

namespace AiAssistant.Core.Models;

public record ConversationTurn
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Role { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public IDictionary<string, object?>? Metadata { get; init; }
    public string? SerializedContentBlocks { get; init; }
    public string? OriginalPrompt { get; init; }
}
