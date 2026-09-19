using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Models;

namespace AiAssistant.Core.Services;

/// <summary>
/// Manages the lifecycle of a Plan, including persistence and state transitions.
/// </summary>
public interface IPlanService
{
    /// <summary>
    /// Gets the currently active plan in the workspace, if any.
    /// </summary>
    Task<Plan?> GetActivePlanAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised whenever the active plan (or its tasks) is updated.
    /// </summary>
    event EventHandler<Plan> PlanUpdated;

    /// <summary>
    /// Proposes a new plan and saves it as the active draft.
    /// </summary>
    Task<Plan> ProposePlanAsync(string sessionId, Plan plan, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revises the current plan based on user feedback.
    /// </summary>
    Task<Plan> RevisePlanAsync(string sessionId, Plan revisedPlan, CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves the current plan, transitioning it to the Approved state.
    /// </summary>
    Task ApprovePlanAsync(string sessionId, string planId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts execution of the plan.
    /// </summary>
    Task StartExecutionAsync(string sessionId, string planId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the generated tasks for the approved plan.
    /// </summary>
    Task GenerateTasksAsync(string sessionId, string planId, System.Collections.Generic.List<PlanTask> tasks, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts execution of a specific task.
    /// </summary>
    Task StartTaskAsync(string sessionId, string planId, string taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes a specific task.
    /// </summary>
    Task CompleteTaskAsync(string sessionId, string planId, string taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a specific task as failed with an error message.
    /// </summary>
    Task FailTaskAsync(string sessionId, string planId, string taskId, string failureMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Logs a tool execution against the active plan.
    /// </summary>
    Task LogExecutionAsync(string sessionId, string planId, ExecutionLogEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses execution to ask the user a question.
    /// </summary>
    Task<AgentQuestionResult> AskQuestionAsync(string sessionId, string planId, AgentQuestion question, CancellationToken cancellationToken = default);

    /// <summary>
    /// Provides an answer to an open question, allowing execution to resume.
    /// </summary>
    Task AnswerQuestionAsync(string sessionId, string planId, string questionId, AgentQuestionResult result, CancellationToken cancellationToken = default);
}
