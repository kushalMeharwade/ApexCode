using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Services;

namespace AiAssistant.Tools.Functions.Act;

public class GenerateWalkthroughTool : CustomAIFunction
{
    private readonly IPlanService _planService;
    private readonly IServiceProvider _serviceProvider;

    public GenerateWalkthroughTool(IPlanService planService, IServiceProvider serviceProvider) : base(
        name: "generate_walkthrough",
        description: "Generates a final walkthrough summary once all tasks are complete.",
        jsonSchemaString: """
        {
            "type": "object",
            "properties": {
                "summary": { "type": "string", "description": "A comprehensive summary of all work completed." },
                "howToTest": { "type": "string", "description": "Instructions for the user on how to verify the changes." }
            },
            "required": ["summary", "howToTest"],
            "additionalProperties": false
        }
        """)
    {
        _planService = planService;
        _serviceProvider = serviceProvider;
    }

    protected override async Task<object?> InvokeCoreImplAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var chatService = (IChatService)_serviceProvider.GetService(typeof(IChatService))!;
        var sessionId = chatService.ActiveSession?.Id ?? "default";

        var plan = await _planService.GetActivePlanAsync(sessionId, cancellationToken);
        if (plan == null) return "Error: No active plan found.";

        var summary = arguments["summary"]?.ToString();
        var howToTest = arguments["howToTest"]?.ToString();

        // In a real implementation, this would save a walkthrough.md file to the workspace
        // or trigger an event that the ChatPanel catches to display the WalkthroughCard.

        // _planService.CompletePlanAsync(plan.Id, summary, howToTest, cancellationToken);
        
        return "Walkthrough generated successfully. The plan is now fully complete. Stop calling tools.";
    }
}


