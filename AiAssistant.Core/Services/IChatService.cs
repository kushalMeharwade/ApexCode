using AiAssistant.Core.Models;
using AiAssistant.Storage.Models;

namespace AiAssistant.Core.Services;

/// <summary>
/// High-level chat orchestration service.
/// Connects the UI to the LLM, manages conversation history, tool execution, and streaming.
/// </summary>
public interface IChatService
{
    /// <summary>
    /// Fired when a new message is received (user or assistant).
    /// </summary>
    event EventHandler<ChatMessage>? MessageReceived;

    /// <summary>
    /// Fired when a streaming token arrives. Content is the accumulated text so far.
    /// </summary>
    event EventHandler<ChatMessage>? StreamingUpdated;

    /// <summary>
    /// Fired when streaming completes for a response.
    /// </summary>
    event EventHandler<ChatMessage>? StreamingCompleted;

    /// <summary>
    /// Fired when an error occurs during chat.
    /// </summary>
    event EventHandler<string>? ErrorOccurred;
    event EventHandler<AgentPause>? AgentPaused;
    Task<AgentPause?> GetPausedTurnAsync(string sessionId);

    /// <summary>
    /// Fired when context token usage changes. Tuple is (CurrentTokens, MaxTokens).
    /// </summary>
    event EventHandler<(int Current, int Max)>? TokenUsageChanged;

    /// <summary>
    /// Fired when the AI client initiates a retry (e.g. due to rate limits or network issues).
    /// </summary>
    event EventHandler<(TimeSpan Delay, string Reason)>? RetryInitiated;

    /// <summary>
    /// Fired when the LLM requests a tool execution.
    /// </summary>
    event EventHandler<ToolCallInfo>? ToolExecuting;

    /// <summary>
    /// Fired when a tool completes execution.
    /// </summary>
    event EventHandler<ToolResultInfo>? ToolCompleted;

    /// <summary>
    /// Fired when the agent proposes a structured plan using the plan mode tool.
    /// </summary>
    event EventHandler<IReadOnlyList<PlanItem>>? PlanProposed;

    /// <summary>
    /// Fired when a checkpoint is created during AI execution.
    /// </summary>
    event EventHandler<CheckpointInfo>? CheckpointCreated;

    // --- Enhanced Plan System Events ---
    event EventHandler<Plan> EnhancedPlanProposed;
    event EventHandler<Plan> EnhancedQuestionAsked;
    event EventHandler<Plan> EnhancedWalkthroughGenerated;
    event EventHandler<Plan> EnhancedPlanUpdated;
    event EventHandler<string> ScrollToActivePlanRequested;

    /// <summary>
    /// Fired when the parallel task execution batch completes (all tasks done or retries exhausted).
    /// Payload contains the plan with final task statuses for failure reporting.
    /// </summary>
    event EventHandler<Plan> ExecutionSummaryReady;

    /// <summary>
    /// The currently active session, or null if none.
    /// </summary>
    Session? ActiveSession { get; }

    /// <summary>
    /// The active mode of the chat service (e.g. "Plan" or "Act").
    /// </summary>
    string ActiveMode { get; set; }

    /// <summary>
    /// Error message if the checkpoint system failed to initialize.
    /// </summary>
    string? CheckpointError { get; }

    /// <summary>
    /// Send a user message and get an AI response (streaming).
    /// </summary>
    Task SendMessageAsync(string sessionId, string userMessage, string? originalPrompt = null, CancellationToken ct = default, List<string>? explicitFiles = null, List<string>? dependencyFiles = null);

    /// <summary>
    /// Get all messages for a session.
    /// </summary>
    Task<IReadOnlyList<ChatMessage>> GetSessionMessagesAsync(string sessionId);

    /// <summary>
    /// Get all sessions.
    /// </summary>
    Task<IReadOnlyList<Session>> GetSessionsAsync();

    /// <summary>Returns history previews without loading full conversations.</summary>
    Task<IReadOnlyList<SessionSummary>> GetSessionSummariesAsync();

    /// <summary>
    /// Get all checkpoints for the active session.
    /// </summary>
    Task<IReadOnlyList<CheckpointInfo>> GetCheckpointsAsync(string sessionId);

    /// <summary>
    /// Restore workspace to a previous checkpoint.
    /// </summary>
    Task<(bool Success, string ErrorMessage)> RestoreCheckpointAsync(string checkpointId, string restoreType, CancellationToken ct = default);

    /// <summary>
    /// Create a new session.
    /// </summary>
    Task<Session> CreateSessionAsync(string name, string providerId, string modelId, string systemPromptId);

    /// <summary>
    /// Delete a session.
    /// </summary>
    Task DeleteSessionAsync(string sessionId);

    /// <summary>
    /// Rename a session.
    /// </summary>
    Task RenameSessionAsync(string sessionId, string newName);

    /// <summary>
    /// Set the active session.
    /// </summary>
    Task SetActiveSessionAsync(string sessionId);

    /// <summary>
    /// Update the session's provider and model.
    /// </summary>
    Task UpdateSessionModelAsync(string sessionId, string providerId, string modelId);

    /// <summary>
    /// Refreshes the model cache for a provider by calling its API.
    /// Called after a provider is first configured or its API key is updated.
    /// </summary>
    Task RefreshModelCacheAsync(string providerId, string apiKey);

    // --- Enhanced Plan System Orchestration ---
    void RaisePlanProposedEvent(Plan plan);
    void RaiseQuestionAskedEvent(Plan plan);
    void RaiseWalkthroughGeneratedEvent(Plan plan);
    void RaiseScrollToPlanEvent(string planId);
    Task TriggerSystemPromptInjectionAsync(CancellationToken cancellationToken);
    Task TriggerAutoExecutionStepAsync(CancellationToken cancellationToken);
    Task TriggerParallelExecutionAsync(string sessionId, Plan plan, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the dynamically calculated character budget for file context injection.
    /// </summary>
    Task<int> GetFileContextBudgetCharsAsync();

    /// <summary>
    /// Returns true if the last assistant turn in this session ended with unresolved
    /// tool calls (i.e., the agent was interrupted before tools could return results).
    /// </summary>
    Task<bool> HasUnresolvedToolCallsAsync(string sessionId);
}
