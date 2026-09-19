using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Services;

namespace AiAssistant.Tools.Functions.Act;

public class StartTaskTool : CustomAIFunction
{
    private readonly IPlanService _planService;
    private readonly IServiceProvider _serviceProvider;

    public StartTaskTool(IPlanService planService, IServiceProvider serviceProvider) : base(
        name: "start_task",
        description: "Marks a task from the generated checklist as InProgress. You must call this before beginning work on a specific task.",
        jsonSchemaString: """
        {
            "type": "object",
            "properties": {
                "taskId": { "type": "string", "description": "The ID of the task you are starting (e.g., 'task-1')." }
            },
            "required": ["taskId"],
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

        var taskId = arguments["taskId"]?.ToString();
        if (string.IsNullOrEmpty(taskId)) return "Error: taskId is required.";

        var task = plan.Tasks.Find(t => t.Id == taskId);
        if (task == null) return $"Error: Task {taskId} not found in the plan.";
        if (task.Status == AiAssistant.Core.Models.TaskStatus.Completed) return $"Error: Task {taskId} is already completed.";

        if (plan.Tasks.Any(t => t.Status == AiAssistant.Core.Models.TaskStatus.InProgress))
        {
            return $"Error: Another task is currently running. You must complete or abort it before starting a new one.";
        }

        if (task.Dependencies != null)
        {
            foreach (var depId in task.Dependencies)
            {
                var depTask = plan.Tasks.Find(t => t.Id == depId);
                if (depTask == null)
                {
                    return $"Error: Cannot start {taskId}. Dependency {depId} was not found in the plan.";
                }
                if (depTask.Status != AiAssistant.Core.Models.TaskStatus.Completed)
                {
                    return $"Error: Cannot start {taskId}. Dependency {depId} is not completed.";
                }
            }
        }

        await _planService.StartTaskAsync(sessionId, plan.Id, taskId, cancellationToken);
        
        return $"Task {taskId} started. You may now use file modification or read tools to execute the task.";
    }
}


