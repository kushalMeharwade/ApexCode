namespace AiAssistant.Tools.Functions;

public class ApprovalRequiredAIFunction
{
    private readonly string _functionName;
    private readonly string _description;

    public ApprovalRequiredAIFunction(string functionName, string description)
    {
        _functionName = functionName;
        _description = description;
    }

    public string FunctionName => _functionName;
    public string Description => _description;
}


