namespace AiAssistant.Core.Models;

public class AgentQuestionResult
{
    public bool WasCancelled { get; set; }
    public string? SelectedOption { get; set; }
    public string? CustomText { get; set; }
}
