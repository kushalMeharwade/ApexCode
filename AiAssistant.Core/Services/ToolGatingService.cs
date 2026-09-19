using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AiAssistant.Core.Models;

namespace AiAssistant.Core.Services;

public interface IToolGatingService
{
    Task<List<string>> FilterToolsAsync(string sessionId, List<string> toolNames);
    Task<bool> CanStartTaskAsync(string sessionId, string taskId);
}

public class ToolGatingService : IToolGatingService
{
    private readonly IPlanService _planService;

    // Destructive tools that edit files
    private static readonly HashSet<string> ExecutionTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "create_file",
        "replace_file_content",
        "multi_replace_file_content",
        "delete_file",
        "rename_file"
    };

    public ToolGatingService(IPlanService planService)
    {
        _planService = planService;
    }

    public async Task<List<string>> FilterToolsAsync(string sessionId, List<string> toolNames)
    {
        var plan = await _planService.GetActivePlanAsync(sessionId);
        var status = plan?.Status ?? PlanStatus.Draft;

        var removed = new List<string>();

        if (status == PlanStatus.Draft || status == PlanStatus.AwaitingReview || status == PlanStatus.RevisionRequested)
        {
            // Allow revise_plan and propose_plan, restrict execution tools
            removed.AddRange(toolNames.Where(t => ExecutionTools.Contains(t)));
            toolNames.RemoveAll(t => ExecutionTools.Contains(t));
            
            // Remove start_task, complete_task, generate_tasks since they are for later phases
            removed.AddRange(toolNames.Where(t => t == "start_task" || t == "complete_task" || t == "generate_tasks"));
            toolNames.RemoveAll(t => t == "start_task" || t == "complete_task" || t == "generate_tasks");
            
            // We want revise_plan to be available, so we don't remove it.
        }
        else if (status == PlanStatus.Approved)
        {
            // Restrict execution tools until generate_tasks is called
            removed.AddRange(toolNames.Where(t => ExecutionTools.Contains(t)));
            toolNames.RemoveAll(t => ExecutionTools.Contains(t));
            
            // Restrict start_task and complete_task
            removed.AddRange(toolNames.Where(t => t == "start_task" || t == "complete_task"));
            toolNames.RemoveAll(t => t == "start_task" || t == "complete_task");
            
            // Allow generate_tasks
        }
        else if (status == PlanStatus.TasksGenerated || status == PlanStatus.InProgress)
        {
            // Parallel execution: tools are always available when tasks are generated/in-progress.
            // Multiple tasks may run concurrently; do NOT restrict based on running task count.
        }
        else if (status == PlanStatus.Completed)
        {
            // Restrict execution tools
            removed.AddRange(toolNames.Where(t => ExecutionTools.Contains(t)));
            toolNames.RemoveAll(t => ExecutionTools.Contains(t));
        }

        return removed;
    }

    public async Task<bool> CanStartTaskAsync(string sessionId, string taskId)
    {
        var plan = await _planService.GetActivePlanAsync(sessionId);
        if (plan == null || plan.Tasks == null) return false;
        
        // Parallel execution: allow starting a task as long as all its declared dependencies are completed.
        var task = plan.Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task == null) return false;
        if (task.Dependencies == null || task.Dependencies.Count == 0) return true;
        return task.Dependencies.All(depId =>
            plan.Tasks.Any(t => t.Id == depId && t.Status == AiAssistant.Core.Models.TaskStatus.Completed));
    }
}
