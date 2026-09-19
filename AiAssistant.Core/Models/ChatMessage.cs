using System.Collections.Generic;

namespace AiAssistant.Core.Models;

/// <summary>
/// Represents a single message in a chat conversation.
/// Used by ChatService for orchestration and by the UI layer for display.
/// </summary>
public record ChatMessage(
    string Id,
    string Role,
    string Content,
    DateTime Timestamp,
    bool IsStreaming = false,
    string? ToolExecutionStatus = null,
    IReadOnlyList<ContentBlock>? ContentBlocks = null,
    bool WasCancelled = false,
    string? OriginalPrompt = null,
    IDictionary<string, object?>? Metadata = null
);
