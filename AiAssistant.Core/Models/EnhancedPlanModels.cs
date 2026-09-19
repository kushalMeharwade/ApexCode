using System;
using System.Collections.Generic;

namespace AiAssistant.Core.Models;

public enum PlanStatus
{
    Draft,
    AwaitingReview,
    RevisionRequested,
    Approved,
    TasksGenerated,
    InProgress,
    AwaitingAnswers,
    Completed,
    Superseded
}

public enum TaskStatus
{
    Pending,
    InProgress,
    Blocked,
    Completed,
    Failed,
    Skipped,
    Interrupted
}

public class Plan : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    // Runtime ownership for filtering asynchronous updates after a session switch.
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SessionId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    
    private PlanStatus _status = PlanStatus.Draft;
    public PlanStatus Status { get => _status; set { _status = value; OnPropertyChanged(); } }
    
    public int Revision { get; set; } = 1;
    
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedAtUtc { get; set; }
    
    public List<string> ImportantChanges { get; set; } = new();
    public List<ModifiedFile> ModifiedFiles { get; set; } = new();
    public List<string> Decisions { get; set; } = new();
    public List<string> Risks { get; set; } = new();
    
    private List<PlanTask> _tasks = new();
    public List<PlanTask> Tasks
    {
        get => _tasks;
        set { _tasks = value ?? new(); OnPropertyChanged(); }
    }
    public List<AgentQuestion> OpenQuestions { get; set; } = new();
    
    private double _taskProgressValue;
    public double TaskProgressValue { get => _taskProgressValue; set { _taskProgressValue = value; OnPropertyChanged(); } }
    
    private string _taskProgressText = string.Empty;
    public string TaskProgressText { get => _taskProgressText; set { _taskProgressText = value; OnPropertyChanged(); } }
}

public class PlanTask : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Dependencies { get; set; } = new(); // IDs of other tasks
    
    private TaskStatus _status = TaskStatus.Pending;
    public TaskStatus Status { get => _status; set { _status = value; OnPropertyChanged(); } }
    
    public List<string> ToolsUsed { get; set; } = new();
    
    private int _attemptNumber = 1;
    public int AttemptNumber { get => _attemptNumber; set { _attemptNumber = value; OnPropertyChanged(); } }
    
    private string? _failureMessage;
    public string? FailureMessage { get => _failureMessage; set { _failureMessage = value; OnPropertyChanged(); } }
}

public class ModifiedFile
{
    public string Path { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty; // Create, Modify, Delete
    public string Description { get; set; } = string.Empty;
}

public enum QuestionState
{
    Pending,
    Answered,
    Cancelled
}

public class AgentQuestion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TaskId { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string? Answer { get; set; }
    public DateTime AskedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? AnsweredAtUtc { get; set; }
    
    public List<string> Options { get; set; } = new();
    public bool AllowCustomAnswer { get; set; } = true;
    public string SubmitLabel { get; set; } = "Submit answer";
    public QuestionState State { get; set; } = QuestionState.Pending;
}

public class PlanFeedback
{
    public string Section { get; set; } = string.Empty; // General, Title, Tasks, etc.
    public string Comment { get; set; } = string.Empty;
}

public class ExecutionLogEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string TaskId { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
}
