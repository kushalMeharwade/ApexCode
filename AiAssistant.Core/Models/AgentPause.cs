namespace AiAssistant.Core.Models;
public enum AgentPauseReason { RecoveryLimit, InvalidToolArguments, ToolFailure, Timeout }
public sealed class AgentPause
{
    public string SessionId { get; set; } = "";
    public AgentPauseReason Reason { get; set; }
    public string Summary { get; set; } = "";
    public string Details { get; set; } = "";
    public string RecoveryPrompt => "Continue the original task from the preserved conversation and tool results. " +
        "Review the previous failure and correct arguments using the available tool schemas. " +
        "Do not repeat failed calls unchanged or redo completed work. " +
        "Inspect current state before retrying any operation whose outcome is uncertain. " +
        "If still blocked, ask for guidance. Previous pause: " + Summary + "\n" + Details;
}
