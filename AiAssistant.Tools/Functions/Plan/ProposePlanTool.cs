using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Models;
using AiAssistant.Core.Services;

namespace AiAssistant.Tools.Functions.Plan;

public class ProposePlanTool : CustomAIFunction
{
    private readonly IPlanService _planService;
    private readonly IEnhancedPlanOrchestrator _orchestrator;
    private readonly IServiceProvider _serviceProvider;

    public ProposePlanTool(IPlanService planService, IEnhancedPlanOrchestrator orchestrator, IServiceProvider serviceProvider) : base(
        name: "propose_plan",
        description: "Proposes a new architectural or execution plan for the user's request. Must be called after analyzing the codebase.",
        jsonSchemaString: """
        {
            "type": "object",
            "properties": {
                "title": {
                    "type": "string",
                    "description": "Short imperative title for the plan."
                },
                "summary": {
                    "type": "string",
                    "description": "2-4 sentences: what changes and why."
                },
                "importantChanges": {
                    "type": "string",
                    "description": "One concrete code change per line (plain text, no bullets, no numbering). Each line is one major architectural or logic change."
                },
                "files": {
                    "type": "string",
                    "description": "One file per line. Format: <path> | <action> | <description>. The action must be exactly one of: Create, Modify, or Delete. Each file must be on a single line. Example: Services/ChatService.cs | Modify | Add retry logic for transient failures"
                },
                "decisions": {
                    "type": "string",
                    "description": "Technical choices made and why, one per line (e.g., 'Using ngIf instead of a tab library — no new dependency'). Empty string only if no notable decisions."
                },
                "risks": {
                    "type": "string",
                    "description": "One risk or breaking change per line (plain text). List at least the main risk; empty string only if genuinely low risk."
                }
            },
            "required": ["title", "summary", "importantChanges", "files"],
            "additionalProperties": false
        }
        """)
    {
        _planService = planService;
        _orchestrator = orchestrator;
        _serviceProvider = serviceProvider;
    }

    protected override async Task<object?> InvokeCoreImplAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var plan = new AiAssistant.Core.Models.Plan
        {
            Title   = arguments.TryGetValue("title", out var t) ? t?.ToString() ?? "Untitled Plan" : "Untitled Plan",
            Summary = arguments.TryGetValue("summary", out var s) ? s?.ToString() ?? "" : "",
            Status  = PlanStatus.Draft
        };

        // ── importantChanges — flat string (new) or string[] (legacy) ────────
        if (arguments.TryGetValue("importantChanges", out var changesObj))
            plan.ImportantChanges.AddRange(PlanToolParser.SplitLines(changesObj));

        // ── files — flat pipe-delimited string (new) or object[] (legacy) ────
        // Also accept old parameter name "modifiedFiles" for transition compatibility.
        arguments.TryGetValue("files", out var filesVal);
        if (filesVal == null) arguments.TryGetValue("modifiedFiles", out filesVal);

        if (filesVal != null)
        {
            var filesResult = PlanToolParser.ParseFileLines(filesVal);
            if (filesResult is string errorMessage)
                return errorMessage; // targeted repair instruction — model fixes only the 'files' field
            if (filesResult is List<ModifiedFile> modifiedFiles)
                plan.ModifiedFiles.AddRange(modifiedFiles);
        }

        // ── decisions — flat string (new) or string[] (legacy) ───────────────
        if (arguments.TryGetValue("decisions", out var decisionsObj))
            plan.Decisions.AddRange(PlanToolParser.SplitLines(decisionsObj));

        // ── risks — flat string (new) or string[] (legacy) ───────────────────
        if (arguments.TryGetValue("risks", out var risksObj))
            plan.Risks.AddRange(PlanToolParser.SplitLines(risksObj));

        var chatService = (IChatService)_serviceProvider.GetService(typeof(IChatService))!;
        var sessionId = chatService.ActiveSession?.Id ?? "default";

        await _planService.ProposePlanAsync(sessionId, plan, cancellationToken);
        await _orchestrator.ProcessStateTransitionAsync(plan, cancellationToken);

        return "Plan proposed successfully. The UI will now display it for user review. Stop calling tools and wait for user approval.";
    }
}



