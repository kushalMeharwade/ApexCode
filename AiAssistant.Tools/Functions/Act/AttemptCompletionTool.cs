using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AiAssistant.Tools.Functions.Act;

/// <summary>Declares completion; the chat loop validates session state before invoking it.</summary>
public sealed class AttemptCompletionTool : CustomAIFunction
{
    public AttemptCompletionTool() : base(
        "attempt_completion",
        "Finish the ACT turn only after fulfilling the user's request and performing appropriate verification. " +
        "Complete all active plan tasks first. For blocked work use ask_question. " +
        "Call this tool alone, after receiving the results of all other tools. No plan is required.",
        """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string", "description": "Final user-facing explanation of the completed work." },
            "verification": { "type": "string", "description": "Checks actually performed and their results. Explicitly disclose checks not run and limitations; never invent evidence." }
          },
          "required": ["summary", "verification"],
          "additionalProperties": false
        }
        """) { }

    protected override Task<object?> InvokeCoreImplAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var summary = arguments.TryGetValue("summary", out var s) ? s?.ToString() : null;
        var verification = arguments.TryGetValue("verification", out var v) ? v?.ToString() : null;
        if (string.IsNullOrWhiteSpace(summary) || string.IsNullOrWhiteSpace(verification))
            return Task.FromResult<object?>("Error: A nonempty summary and verification are required.");
        return Task.FromResult<object?>(summary!.Trim() + "\n\nVerification: " + verification!.Trim());
    }
}
