using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Models;
using AiAssistant.Core.Services;

namespace AiAssistant.Tools.Functions.Act;

public class GenerateTasksTool : CustomAIFunction
{
    private readonly IPlanService _planService;
    private readonly IEnhancedPlanOrchestrator _orchestrator;
    private readonly IServiceProvider _serviceProvider;

    public GenerateTasksTool(IPlanService planService, IEnhancedPlanOrchestrator orchestrator, IServiceProvider serviceProvider) : base(
        name: "generate_tasks",
        description: "Generates an execution checklist based on the approved plan. Called immediately after a plan is approved.",
        jsonSchemaString: """
        {
            "type": "object",
            "properties": {
                "tasks": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "id": { "type": "string", "description": "Unique identifier for this task (e.g., 'task-1')" },
                            "title": { "type": "string", "description": "Short name for the task." },
                            "description": { "type": "string", "description": "Detailed description of what needs to be done." },
                            "dependencies": {
                                "type": "array",
                                "items": { "type": "string" },
                                "description": "List of task IDs that must be completed before this one."
                            },
                            "toolsUsed": {
                                "type": "array",
                                "items": { "type": "string" },
                                "description": "List of tool names likely required for this task."
                            }
                        },
                        "required": ["id", "title", "description", "dependencies", "toolsUsed"]
                    }
                }
            },
            "required": ["tasks"],
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
        var chatService = (IChatService)_serviceProvider.GetService(typeof(IChatService))!;
        var sessionId = chatService.ActiveSession?.Id ?? "default";

        var plan = await _planService.GetActivePlanAsync(sessionId, cancellationToken);
        if (plan == null) return "Error: No active plan found.";

        var tasks = new List<PlanTask>();

        if (arguments.TryGetValue("tasks", out var tasksObj) && tasksObj is JsonElement tasksArray)
        {
            foreach (var item in tasksArray.EnumerateArray())
            {
                var deps = new List<string>();
                foreach (var dep in item.GetProperty("dependencies").EnumerateArray()) deps.Add(dep.GetString()!);
                
                var tools = new List<string>();
                foreach (var tool in item.GetProperty("toolsUsed").EnumerateArray()) tools.Add(tool.GetString()!);

                tasks.Add(new PlanTask
                {
                    Id = item.GetProperty("id").GetString()!,
                    Title = item.GetProperty("title").GetString()!,
                    Description = item.GetProperty("description").GetString()!,
                    Dependencies = deps,
                    ToolsUsed = tools,
                    Status = AiAssistant.Core.Models.TaskStatus.Pending
                });
            }
        }

        await _planService.GenerateTasksAsync(sessionId, plan.Id, tasks, cancellationToken);
        
        if (tasks.Count == 0)
        {
            plan.Status = PlanStatus.Completed;
            await _orchestrator.ProcessStateTransitionAsync(plan, cancellationToken);
            return "No tasks were generated. The plan is considered completed.";
        }
        else
        {
            plan.Status = PlanStatus.TasksGenerated;
            await _orchestrator.ProcessStateTransitionAsync(plan, cancellationToken);
            return "Tasks generated successfully. Execution can now begin.";
        }
    }
}


