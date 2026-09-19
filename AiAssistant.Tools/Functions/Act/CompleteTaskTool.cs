using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Models;
using AiAssistant.Core.Services;

namespace AiAssistant.Tools.Functions.Act;

public class CompleteTaskTool : CustomAIFunction
{
    private readonly IPlanService _planService;
    private readonly IEnhancedPlanOrchestrator _orchestrator;
    private readonly IServiceProvider _serviceProvider;

    public CompleteTaskTool(IPlanService planService, IEnhancedPlanOrchestrator orchestrator, IServiceProvider serviceProvider) : base(
        name: "complete_task",
        description: "Marks a task as Completed. You must call this when you have finished all work for a specific task.",
        jsonSchemaString: """
        {
            "type": "object",
            "properties": {
                "taskId": { "type": "string", "description": "The ID of the task you have completed." },
                "summary": { "type": "string", "description": "A brief summary of what was done to complete this task." }
            },
            "required": ["taskId", "summary"],
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

        var taskId = arguments["taskId"]?.ToString();
        if (string.IsNullOrEmpty(taskId)) return "Error: taskId is required.";

        var task = plan.Tasks.Find(t => t.Id == taskId);
        if (task == null) return $"Error: Task {taskId} not found in the plan.";
        if (task.Status == AiAssistant.Core.Models.TaskStatus.Completed) return $"Error: Task {taskId} is already completed.";

        if (task.Dependencies != null)
        {
            foreach (var depId in task.Dependencies)
            {
                var depTask = plan.Tasks.Find(t => t.Id == depId);
                if (depTask == null)
                {
                    return $"Error: Cannot complete {taskId}. Dependency {depId} was not found in the plan.";
                }
                if (depTask.Status != AiAssistant.Core.Models.TaskStatus.Completed)
                {
                    return $"Error: Cannot complete {taskId}. Dependency {depId} is not completed.";
                }
            }
        }

        await _planService.CompleteTaskAsync(sessionId, plan.Id, taskId, cancellationToken);
        
        // Reload to check if all tasks are complete
        var updatedPlan = await _planService.GetActivePlanAsync(sessionId, cancellationToken);
        if (updatedPlan != null && updatedPlan.Tasks.TrueForAll(t => t.Status == AiAssistant.Core.Models.TaskStatus.Completed))
        {
            updatedPlan.Status = PlanStatus.Completed;
            await _orchestrator.ProcessStateTransitionAsync(updatedPlan, cancellationToken);
            return $"Task {taskId} marked as completed. All tasks are finished! The plan is now complete.";
        }
        
        return $"Task {taskId} marked as completed. You may now proceed to the next task.";
    }
}


