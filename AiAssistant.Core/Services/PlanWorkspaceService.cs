using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Models;

namespace AiAssistant.Core.Services;

public class PlanWorkspaceService : IPlanService
{
    private readonly string _workspaceRoot;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AgentQuestionResult>> _pendingQuestions = new();

    public event EventHandler<Plan>? PlanUpdated;

    public PlanWorkspaceService(string workspaceRoot)
    {
        _workspaceRoot = workspaceRoot;
        _jsonOptions = new JsonSerializerOptions 
        { 
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    private string GetSessionDir(string sessionId)
    {
        return Path.Combine(_workspaceRoot, ".aiassistant", "plans", sessionId);
    }

    private void EnsureSessionDirectoryExists(string sessionId)
    {
        var dir = GetSessionDir(sessionId);
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public async Task<Plan?> GetActivePlanAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var dir = GetSessionDir(sessionId);
            var jsonPath = Path.Combine(dir, "plan.json");
            var mdPath = Path.Combine(dir, "plan.md");
            Plan? plan = null;

            if (File.Exists(jsonPath))
            {
                var json = File.ReadAllText(jsonPath);
                plan = JsonSerializer.Deserialize<Plan>(json, _jsonOptions);
            }
            else if (File.Exists(mdPath)) // Graceful fallback for legacy plans
            {
                var lines = File.ReadAllLines(mdPath);
                var title = lines.FirstOrDefault(l => l.StartsWith("# "))?.Substring(2) ?? "Active Plan";
                var statusLine = lines.FirstOrDefault(l => l.StartsWith("Status: "));
                var statusStr = statusLine?.Substring(8);
                Enum.TryParse<PlanStatus>(statusStr, out var status);
                plan = new Plan { Id = "active", Title = title, Status = status };
            }
            
            // Still check old global plan.md just in case (legacy migration)
            if (plan == null && sessionId == "default")
            {
                var globalMdPath = Path.Combine(_workspaceRoot, ".aiassistant", "plans", "plan.md");
                if (File.Exists(globalMdPath))
                {
                    var lines = File.ReadAllLines(globalMdPath);
                    var title = lines.FirstOrDefault(l => l.StartsWith("# "))?.Substring(2) ?? "Active Plan";
                    var statusLine = lines.FirstOrDefault(l => l.StartsWith("Status: "));
                    var statusStr = statusLine?.Substring(8);
                    Enum.TryParse<PlanStatus>(statusStr, out var status);
                    plan = new Plan { Id = "active", Title = title, Status = status };
                }
            }

            if (plan == null) return null;

            var tasksPath = Path.Combine(dir, "tasks.json");
            if (File.Exists(tasksPath))
            {
                var tasksJson = File.ReadAllText(tasksPath);
                var tasks = JsonSerializer.Deserialize<List<PlanTask>>(tasksJson, _jsonOptions);
                if (tasks != null)
                {
                    bool tasksChanged = false;
                    foreach (var task in tasks)
                    {
                        if (task.Status == AiAssistant.Core.Models.TaskStatus.InProgress)
                        {
                            task.Status = AiAssistant.Core.Models.TaskStatus.Interrupted;
                            task.FailureMessage = "Task was interrupted by a session restart or crash.";
                            tasksChanged = true;
                        }
                    }

                    if (tasksChanged)
                    {
                        // Save the repaired tasks back to disk immediately
                        File.WriteAllText(tasksPath, JsonSerializer.Serialize(tasks, _jsonOptions));
                    }

                    plan.Tasks = tasks;
                    if (tasks.Count > 0)
                    {
                        int completed = tasks.Count(t => t.Status == AiAssistant.Core.Models.TaskStatus.Completed);
                        plan.TaskProgressValue = ((double)completed / tasks.Count) * 100;
                        plan.TaskProgressText = $"{completed} of {tasks.Count} Tasks Completed";
                    }
                }
            }
            
            var questionsPath = Path.Combine(dir, "questions.json");
            if (File.Exists(questionsPath))
            {
                var questionsJson = File.ReadAllText(questionsPath);
                var questions = JsonSerializer.Deserialize<List<AgentQuestion>>(questionsJson, _jsonOptions);
                if (questions != null)
                {
                    plan.OpenQuestions = questions;
                }
            }
            
            if (plan != null) plan.SessionId = sessionId;
            return plan;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private void NotifyPlanUpdated(string sessionId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var plan = await GetActivePlanAsync(sessionId);
                if (plan != null)
                {
                    PlanUpdated?.Invoke(this, plan);
                }
            }
            catch (Exception) { /* Ignore errors */ }
        });
    }

    private void SavePlanInternal(string sessionId, Plan plan)
    {
        plan.SessionId = sessionId;
        EnsureSessionDirectoryExists(sessionId);
        var jsonPath = Path.Combine(GetSessionDir(sessionId), "plan.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(plan, _jsonOptions));
    }

    public async Task<Plan> ProposePlanAsync(string sessionId, Plan plan, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            // Supersede existing plan if it is still AwaitingReview
            var dir = GetSessionDir(sessionId);
            var jsonPath = Path.Combine(dir, "plan.json");
            if (File.Exists(jsonPath))
            {
                var existingJson = File.ReadAllText(jsonPath);
                var existingPlan = JsonSerializer.Deserialize<Plan>(existingJson, _jsonOptions);
                if (existingPlan != null && existingPlan.Status == PlanStatus.AwaitingReview)
                {
                    existingPlan.Status = PlanStatus.Superseded;
                    File.WriteAllText(jsonPath, JsonSerializer.Serialize(existingPlan, _jsonOptions));
                }
            }

            plan.Status = PlanStatus.AwaitingReview;
            SavePlanInternal(sessionId, plan);
            NotifyPlanUpdated(sessionId);
            return plan;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<Plan> RevisePlanAsync(string sessionId, Plan revisedPlan, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            revisedPlan.Status = PlanStatus.AwaitingReview;
            SavePlanInternal(sessionId, revisedPlan);
            NotifyPlanUpdated(sessionId);
            return revisedPlan;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task ApprovePlanAsync(string sessionId, string planId, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var jsonPath = Path.Combine(GetSessionDir(sessionId), "plan.json");
            if (File.Exists(jsonPath))
            {
                var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(jsonPath), _jsonOptions);
                if (plan != null)
                {
                    plan.Status = PlanStatus.Approved;
                    File.WriteAllText(jsonPath, JsonSerializer.Serialize(plan, _jsonOptions));
                    NotifyPlanUpdated(sessionId);
                }
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task StartExecutionAsync(string sessionId, string planId, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var jsonPath = Path.Combine(GetSessionDir(sessionId), "plan.json");
            if (File.Exists(jsonPath))
            {
                var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(jsonPath), _jsonOptions);
                if (plan != null)
                {
                    plan.Status = PlanStatus.InProgress;
                    File.WriteAllText(jsonPath, JsonSerializer.Serialize(plan, _jsonOptions));
                    NotifyPlanUpdated(sessionId);
                }
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task GenerateTasksAsync(string sessionId, string planId, List<PlanTask> tasks, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            EnsureSessionDirectoryExists(sessionId);
            var tasksPath = Path.Combine(GetSessionDir(sessionId), "tasks.json");
            File.WriteAllText(tasksPath, JsonSerializer.Serialize(tasks, _jsonOptions));
            
            var jsonPath = Path.Combine(GetSessionDir(sessionId), "plan.json");
            if (File.Exists(jsonPath))
            {
                var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(jsonPath), _jsonOptions);
                if (plan != null)
                {
                    plan.Status = PlanStatus.TasksGenerated;
                    File.WriteAllText(jsonPath, JsonSerializer.Serialize(plan, _jsonOptions));
                }
            }
            NotifyPlanUpdated(sessionId);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task StartTaskAsync(string sessionId, string planId, string taskId, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var tasksPath = Path.Combine(GetSessionDir(sessionId), "tasks.json");
            if (File.Exists(tasksPath))
            {
                var tasks = JsonSerializer.Deserialize<List<PlanTask>>(File.ReadAllText(tasksPath), _jsonOptions);
                var task = tasks?.FirstOrDefault(t => t.Id == taskId);
                if (task != null)
                {
                    if (task.Dependencies != null)
                    {
                        foreach (var depId in task.Dependencies)
                        {
                            var depTask = tasks.FirstOrDefault(t => t.Id == depId);
                            if (depTask == null || depTask.Status != AiAssistant.Core.Models.TaskStatus.Completed)
                            {
                                throw new InvalidOperationException($"Cannot start task {taskId}: Dependency {depId} is missing or not completed.");
                            }
                        }
                    }
                    task.Status = AiAssistant.Core.Models.TaskStatus.InProgress;
                    File.WriteAllText(tasksPath, JsonSerializer.Serialize(tasks, _jsonOptions));
                    NotifyPlanUpdated(sessionId);
                }
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task CompleteTaskAsync(string sessionId, string planId, string taskId, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var tasksPath = Path.Combine(GetSessionDir(sessionId), "tasks.json");
            if (File.Exists(tasksPath))
            {
                var tasks = JsonSerializer.Deserialize<List<PlanTask>>(File.ReadAllText(tasksPath), _jsonOptions);
                var task = tasks?.FirstOrDefault(t => t.Id == taskId);
                if (task != null)
                {
                    if (task.Dependencies != null)
                    {
                        foreach (var depId in task.Dependencies)
                        {
                            var depTask = tasks.FirstOrDefault(t => t.Id == depId);
                            if (depTask == null || depTask.Status != AiAssistant.Core.Models.TaskStatus.Completed)
                            {
                                throw new InvalidOperationException($"Cannot complete task {taskId}: Dependency {depId} is missing or not completed.");
                            }
                        }
                    }
                    task.Status = AiAssistant.Core.Models.TaskStatus.Completed;
                    File.WriteAllText(tasksPath, JsonSerializer.Serialize(tasks, _jsonOptions));
                    NotifyPlanUpdated(sessionId);
                }
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task FailTaskAsync(string sessionId, string planId, string taskId, string failureMessage, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var tasksPath = Path.Combine(GetSessionDir(sessionId), "tasks.json");
            if (File.Exists(tasksPath))
            {
                var tasks = JsonSerializer.Deserialize<List<PlanTask>>(File.ReadAllText(tasksPath), _jsonOptions);
                var task = tasks?.FirstOrDefault(t => t.Id == taskId);
                if (task != null)
                {
                    task.Status = AiAssistant.Core.Models.TaskStatus.Failed;
                    task.FailureMessage = failureMessage;
                    File.WriteAllText(tasksPath, JsonSerializer.Serialize(tasks, _jsonOptions));
                    NotifyPlanUpdated(sessionId);
                }
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task LogExecutionAsync(string sessionId, string planId, ExecutionLogEntry entry, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            EnsureSessionDirectoryExists(sessionId);
            var logPath = Path.Combine(GetSessionDir(sessionId), "execution-log.jsonl");
            var json = JsonSerializer.Serialize(entry, _jsonOptions);
            File.AppendAllText(logPath, json + Environment.NewLine);
            NotifyPlanUpdated(sessionId);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<AgentQuestionResult> AskQuestionAsync(string sessionId, string planId, AgentQuestion question, CancellationToken cancellationToken = default)
    {
        if (_pendingQuestions.ContainsKey(sessionId))
        {
            throw new InvalidOperationException("A question is already pending for this session.");
        }

        var tcs = new TaskCompletionSource<AgentQuestionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => 
        {
            _pendingQuestions.TryRemove(sessionId, out _);
            tcs.TrySetCanceled(cancellationToken);
        });
        
        _pendingQuestions[sessionId] = tcs;

        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            EnsureSessionDirectoryExists(sessionId);
            var questionsPath = Path.Combine(GetSessionDir(sessionId), "questions.json");
            List<AgentQuestion> questions = new();
            if (File.Exists(questionsPath))
            {
                var questionsJson = File.ReadAllText(questionsPath);
                questions = JsonSerializer.Deserialize<List<AgentQuestion>>(questionsJson, _jsonOptions) ?? new();
            }
            questions.Add(question);
            File.WriteAllText(questionsPath, JsonSerializer.Serialize(questions, _jsonOptions));

            var jsonPath = Path.Combine(GetSessionDir(sessionId), "plan.json");
            Plan? plan = null;
            if (File.Exists(jsonPath))
            {
                plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(jsonPath), _jsonOptions);
            }
            else
            {
                var mdPath = Path.Combine(GetSessionDir(sessionId), "plan.md");
                if (File.Exists(mdPath))
                {
                    var lines = File.ReadAllLines(mdPath);
                    var title = lines.FirstOrDefault(l => l.StartsWith("# "))?.Substring(2) ?? "Active Plan";
                    plan = new Plan { Id = "active", Title = title, Status = PlanStatus.InProgress };
                }
                else if (sessionId == "default")
                {
                    var globalMdPath = Path.Combine(_workspaceRoot, ".aiassistant", "plans", "plan.md");
                    if (File.Exists(globalMdPath))
                    {
                        var lines = File.ReadAllLines(globalMdPath);
                        var title = lines.FirstOrDefault(l => l.StartsWith("# "))?.Substring(2) ?? "Active Plan";
                        plan = new Plan { Id = "active", Title = title, Status = PlanStatus.InProgress };
                    }
                }
            }
            
            if (plan == null)
            {
                plan = new Plan { Id = "active", Title = "Active Plan", Status = PlanStatus.InProgress };
            }

            plan.Status = PlanStatus.AwaitingAnswers;
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(plan, _jsonOptions));

            NotifyPlanUpdated(sessionId);
        }
        finally
        {
            _fileLock.Release();
        }

        try 
        {
            return await tcs.Task;
        }
        finally
        {
            registration.Dispose();
        }
    }

    public async Task AnswerQuestionAsync(string sessionId, string planId, string questionId, AgentQuestionResult result, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            EnsureSessionDirectoryExists(sessionId);
            
            var questionsPath = Path.Combine(GetSessionDir(sessionId), "questions.json");
            if (File.Exists(questionsPath))
            {
                var questions = JsonSerializer.Deserialize<List<AgentQuestion>>(File.ReadAllText(questionsPath), _jsonOptions);
                var question = questions?.FirstOrDefault(q => q.Id == questionId);
                if (question != null)
                {
                    question.Answer = result.WasCancelled ? "CANCELLED" : (result.SelectedOption ?? result.CustomText);
                    question.State = result.WasCancelled ? QuestionState.Cancelled : QuestionState.Answered;
                    question.AnsweredAtUtc = DateTime.UtcNow;
                    File.WriteAllText(questionsPath, JsonSerializer.Serialize(questions, _jsonOptions));
                }
            }

            var jsonPath = Path.Combine(GetSessionDir(sessionId), "plan.json");
            if (File.Exists(jsonPath))
            {
                var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(jsonPath), _jsonOptions);
                if (plan != null)
                {
                    plan.Status = PlanStatus.InProgress;
                    File.WriteAllText(jsonPath, JsonSerializer.Serialize(plan, _jsonOptions));
                }
            }
            NotifyPlanUpdated(sessionId);
        }
        finally
        {
            _fileLock.Release();
        }

        if (_pendingQuestions.TryRemove(sessionId, out var tcs))
        {
            tcs.TrySetResult(result);
        }
    }
}
