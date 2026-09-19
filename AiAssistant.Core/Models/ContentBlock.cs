using System.Collections.Generic;

namespace AiAssistant.Core.Models;

public class ContentBlock
{
    public AgentPause? Pause { get; set; }
    public string Role { get; set; } = "";
    public string? Text { get; set; }
    public string? CallId { get; set; }
    public string? Name { get; set; }
    public IDictionary<string, object?>? Arguments { get; set; }
    public string? Result { get; set; }
    // Identifies the accepted tool call whose user-facing answer this text represents.
    // Separate from CallId, which pairs tool calls and results.
    public string? CompletionCallId { get; set; }
}
