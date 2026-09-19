using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Models;
using AiAssistant.Core.Pipeline;
using AiAssistant.Core.Services;
using AiAssistant.Engine.Services;
using AiAssistant.Storage.Models;
using AiAssistant.Storage.Repositories;
using AiAssistant.Llm.Services;
using AiAssistant.Tools.Services;
using AiAssistant.Tools.Functions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using ChatMessage = AiAssistant.Core.Models.ChatMessage;

namespace ApexCode.Services;

/// <summary>
/// Chat orchestration service — lives in the VSIX project to avoid circular dependencies.
/// Connects the UI to the LLM, manages conversation history, tool execution, and streaming.
/// </summary>
public class ChatService : IChatService, ICommandProgressSource
{
    private readonly ISessionManager _sessionManager;
    private readonly IChatClientFactory _clientFactory;
    private readonly IToolRegistry _toolRegistry;
    private readonly IContextCompactor _contextCompactor;
    private readonly IChatRequestPipeline _chatRequestPipeline;
    private readonly IRoslynEditService _editService;
    private readonly IDiffPreviewService _diffPreviewService;
    private readonly ICompileValidator _validator;
    private readonly IErrorFixer _errorFixer;
    private readonly IProviderProfileRepository _providerRepo;
    private readonly ICheckpointService _checkpointService;
    private readonly ILogger<ChatService> _logger;
    private readonly IOutputLogger _outputLogger;
    private readonly IStatusBarService _statusBarService;
    private readonly IInfoBarService _infoBarService;
    private readonly IPlanService _planService;
    private readonly IToolGatingService _toolGatingService;
    private readonly ISettingsService _settingsService;
    private readonly IVisualStudioEnvironmentService _vsEnvService;
    private readonly ILogBus _logBus;
    private readonly ITaskLockService _taskLockService;
    private readonly CommandSessionService? _commandSessions;
    public event EventHandler<CommandProgress>? CommandProgress;

    /// <summary>Configure before starting a turn; each turn uses an immutable snapshot.</summary>
    public ChatResilienceOptions ResilienceOptions { get; set; }
    public Session? ActiveSession { get; private set; }

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<ChatMessage>? StreamingUpdated;
    public event EventHandler<ChatMessage>? StreamingCompleted;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<AgentPause>? AgentPaused;
    public async Task<AgentPause?> GetPausedTurnAsync(string sessionId)
    {
        var history = await _sessionManager.GetConversationHistoryAsync(sessionId).ConfigureAwait(false);
        var last = history.LastOrDefault();
        if (last?.Role != "assistant" || string.IsNullOrEmpty(last.SerializedContentBlocks)) return null;
        try { return JsonSerializer.Deserialize<List<ContentBlock>>(last.SerializedContentBlocks!)?.LastOrDefault(b => b.Pause != null)?.Pause; }
        catch (JsonException) { return null; }
    }
    public event EventHandler<(int Current, int Max)>? TokenUsageChanged;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Current, int Max)> _contextUsage = new();
    public event EventHandler<(TimeSpan Delay, string Reason)>? RetryInitiated;
    public event EventHandler<ToolCallInfo>? ToolExecuting;
    public event EventHandler<ToolResultInfo>? ToolCompleted;
    public event EventHandler<IReadOnlyList<PlanItem>>? PlanProposed;
    public event EventHandler<CheckpointInfo>? CheckpointCreated;

    // --- Enhanced Plan System Events ---
    public event EventHandler<Plan>? EnhancedPlanProposed;
    public event EventHandler<Plan>? EnhancedQuestionAsked;
    public event EventHandler<Plan>? EnhancedWalkthroughGenerated;
    public event EventHandler<Plan>? EnhancedPlanUpdated;
    public event EventHandler<string>? ScrollToActivePlanRequested;
    public event EventHandler<Plan>? ExecutionSummaryReady;

    private string _activeMode = AgentModes.Plan;

    /// <summary>
    /// Current agent mode. Normalised on assignment so an unexpected value from the UI resolves to
    /// Plan (read-only) rather than falling through the mode checks and behaving like Act.
    /// </summary>
    public string ActiveMode
    {
        get => _activeMode;
        set
        {
            _activeMode = AgentModes.Normalize(value);
            if (ActiveSession != null && ActiveSession.ActiveMode != _activeMode)
            {
                ActiveSession.ActiveMode = _activeMode;
                _ = _sessionManager.UpdateSessionAsync(ActiveSession);
            }
        }
    }

    public string? CheckpointError => _checkpointService?.InitializationError;

    public ChatService(
        ISessionManager sessionManager,
        IChatClientFactory clientFactory,
        IToolRegistry toolRegistry,
        IContextCompactor contextCompactor,
        IChatRequestPipeline chatRequestPipeline,
        IRoslynEditService editService,
        IDiffPreviewService diffPreviewService,
        ICompileValidator validator,
        IErrorFixer errorFixer,
        IProviderProfileRepository providerRepo,
        ICheckpointService checkpointService,
        ILogger<ChatService> logger,
        IOutputLogger outputLogger,
        IStatusBarService statusBarService,
        IInfoBarService infoBarService,
        ISettingsService settingsService,
        IPlanService planService,
        IToolGatingService toolGatingService,
        IVisualStudioEnvironmentService vsEnvService,
        ILogBus logBus,
        ITaskLockService taskLockService, CommandSessionService? commandSessions = null)
    {
        _commandSessions = commandSessions;
        if (_commandSessions != null) _commandSessions.Progress += (_, progress) => CommandProgress?.Invoke(this, progress);
        _taskLockService = taskLockService ?? throw new ArgumentNullException(nameof(taskLockService));
        _logBus = logBus ?? throw new ArgumentNullException(nameof(logBus));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _contextCompactor = contextCompactor ?? throw new ArgumentNullException(nameof(contextCompactor));
        _chatRequestPipeline = chatRequestPipeline ?? throw new ArgumentNullException(nameof(chatRequestPipeline));
        _editService = editService ?? throw new ArgumentNullException(nameof(editService));
        _diffPreviewService = diffPreviewService ?? throw new ArgumentNullException(nameof(diffPreviewService));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _errorFixer = errorFixer ?? throw new ArgumentNullException(nameof(errorFixer));
        _providerRepo = providerRepo ?? throw new ArgumentNullException(nameof(providerRepo));
        _checkpointService = checkpointService ?? throw new ArgumentNullException(nameof(checkpointService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _outputLogger = outputLogger ?? throw new ArgumentNullException(nameof(outputLogger));
        _statusBarService = statusBarService ?? throw new ArgumentNullException(nameof(statusBarService));
        _infoBarService = infoBarService ?? throw new ArgumentNullException(nameof(infoBarService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        ResilienceOptions = new ChatResilienceOptions
        {
            MaxModelRequests = Math.Max(1, settingsService.MaxToolIterations),
            MaxConsecutiveMistakes = Math.Max(1, settingsService.MaxConsecutiveMistakes),
            MaxEmptyRetries = Math.Max(0, settingsService.MaxAutoRetryAttempts),
            MaxTransportRetries = Math.Max(0, settingsService.MaxStreamRetries)
        };
        _planService = planService ?? throw new ArgumentNullException(nameof(planService));
        _toolGatingService = toolGatingService ?? throw new ArgumentNullException(nameof(toolGatingService));
        _vsEnvService = vsEnvService ?? throw new ArgumentNullException(nameof(vsEnvService));
        if (_planService != null)
        {
            _planService.PlanUpdated += (s, e) => 
            {
                if (!string.Equals(e.SessionId, ActiveSession?.Id, StringComparison.Ordinal)) return;
                EnhancedPlanUpdated?.Invoke(this, e);
                if (e.Status == AiAssistant.Core.Models.PlanStatus.AwaitingAnswers)
                {
                    EnhancedQuestionAsked?.Invoke(this, e);
                }
            };
        }

        _clientFactory.OnRetry += (s, e) =>
        {
            _statusBarService.SetText($"AI {e.Reason}, retrying in {e.Delay.TotalSeconds:F1}s...");
            RetryInitiated?.Invoke(this, (e.Delay, e.Reason));
        };
    }

    public async Task SendMessageAsync(string sessionId, string userMessage, string? originalPrompt = null, CancellationToken ct = default, List<string>? explicitFiles = null, List<string>? dependencyFiles = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(userMessage))
            return;

        using var taskLock = await _taskLockService.AcquireLockAsync(sessionId, ct).ConfigureAwait(false);

        // Snapshot the mode for the whole turn. ActiveMode is a mutable property driven by a UI
        // combo box, and approving a plan flips it to Act while a request may still be in flight —
        // reading it repeatedly could gate tools as Plan and then prompt as Act within one request.
        CommandSessionService.Owner.Value = sessionId;
        var turnMode = AgentModes.Normalize(ActiveMode);

        string? answerContext = null;
        AiAssistant.Core.Models.Plan? activePlan = null;
        if (_settingsService.UseEnhancedPlanSystem)
        {
            activePlan = await _planService.GetActivePlanAsync(sessionId, ct);
            if (activePlan != null && activePlan.Status == AiAssistant.Core.Models.PlanStatus.AwaitingAnswers)
            {
                var question = activePlan.OpenQuestions.FirstOrDefault(q => q.Answer == null);
                if (question != null)
                {
                    var questionResult = new AiAssistant.Core.Models.AgentQuestionResult
                    {
                        WasCancelled = false,
                        SelectedOption = null,
                        CustomText = userMessage
                    };
                    await _planService.AnswerQuestionAsync(sessionId, activePlan.Id, question.Id, questionResult, ct);
                    
                    answerContext = $"\n\n[SYSTEM: The user answered your question (Task {question.TaskId}):\n" +
                                    $"Q: {question.Question}\n" +
                                    $"A: {userMessage}\n" +
                                    $"Please continue execution with this context in mind.]";
                }
            }
        }

        ChatMessage? assistantMessage = null;
        string accumulatedContent = "";
        bool wasCancelled = false;
        bool escalated = false;
        AgentPause? pause = null;
        string? lastToolError = null;
        AgentPauseReason toolPauseReason = AgentPauseReason.InvalidToolArguments;
        bool streamingCompletedFired = false;
        bool assistantTurnStored = false;
        string? acceptedCompletionCallId = null;
        Microsoft.Extensions.AI.ChatMessage? acceptedCompletionMessage = null;
        Microsoft.Extensions.AI.ChatResponse? finalResponse = null;

        // Conversation-level transaction state (initialized inside try, finalized in finally)
        LlmTransaction? conversationTransaction = null;
        LlmTransaction? prevSuppressedTx = null;
        bool hadSuppressedTx = false;
        var allMessagesIncludingTools = new List<Microsoft.Extensions.AI.ChatMessage>();
        var conversationStopwatch = System.Diagnostics.Stopwatch.StartNew();
        conversationStopwatch.Stop();

        // Initialize resilience state for this request
        var resilience = new ChatTurnRecovery(ResilienceOptions);
        var callerToken = ct;
        using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        turnCancellation.CancelAfter(resilience.Options.TurnTimeout);
        ct = turnCancellation.Token;

        void Escalate(string reason, AgentPauseReason pauseReason = AgentPauseReason.RecoveryLimit)
        {
            escalated = true;
            pause = new AgentPause { SessionId = sessionId, Reason = pauseReason, Summary = reason, Details = lastToolError ?? reason };
            accumulatedContent = "Agent paused: " + reason + "\n\nChoose Continue to resume, or add guidance.";
            allMessagesIncludingTools.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, accumulatedContent));
            _logger.LogWarning("Agent turn stopped for session {SessionId}: {Reason}", sessionId, reason);
            // Publish the pause after history and cleanup are finalized.
        }

        void FireStreamingCompleted(bool cancelled)
        {
            if (streamingCompletedFired) return;
            streamingCompletedFired = true;
            assistantMessage ??= new ChatMessage(Guid.NewGuid().ToString(), "assistant", "", DateTime.UtcNow, true);
            assistantMessage = assistantMessage with { IsStreaming = false, Content = accumulatedContent, WasCancelled = cancelled };
            StreamingCompleted?.Invoke(this, assistantMessage);
            var finishReasonStr = finalResponse?.FinishReason != null ? $" [Reason: {finalResponse.FinishReason}]" : "";
            _outputLogger.Log(LogCategory.Chat, $"◄ Response complete ({accumulatedContent.Length} chars){finishReasonStr}{(cancelled ? " [CANCELLED]" : "")}", detail: accumulatedContent);
        }

        try
        {
            // 1. Store user message without the intent block for clean UI history.
            // FIX #1: Hardened regex — uses [^\]] to match the bracket content without risk of
            // accidentally stopping at a ] inside a file path. \s* eats any trailing whitespace
            // so the stored message starts cleanly at the actual user text.
            var cleanUserMessage = System.Text.RegularExpressions.Regex.Replace(
                userMessage, @"\[USER_INTENT:[^\]]*\]\s*", "").TrimStart();

            var metadata = new Dictionary<string, object?>();
            if (explicitFiles != null && explicitFiles.Count > 0)
            {
                metadata["MentionedFiles"] = JsonSerializer.Serialize(explicitFiles);
            }
            if (dependencyFiles != null && dependencyFiles.Count > 0)
            {
                metadata["DependencyFiles"] = JsonSerializer.Serialize(dependencyFiles);
            }

            var userTurn = new ConversationTurn
            {
                Role = "user",
                Content = cleanUserMessage,
                OriginalPrompt = originalPrompt,
                Timestamp = DateTime.UtcNow,
                Metadata = metadata.Count > 0 ? metadata : null
            };
            await _sessionManager.AddMessageAsync(sessionId, userTurn).ConfigureAwait(false);
            
            var msgPreview = cleanUserMessage.Length > 100 ? cleanUserMessage.Substring(0, 100) + "..." : cleanUserMessage;
            _outputLogger.Log(LogCategory.Chat, $"► User → \"{msgPreview}\"", cleanUserMessage);

            // Notify UI
            var userChatMsg = new ChatMessage(userTurn.Id, "user", cleanUserMessage, userTurn.Timestamp);
            MessageReceived?.Invoke(this, userChatMsg);

            // 2. Build conversation context with persona injection
            var providerId = ActiveSession?.ProviderId ?? "openai";
            var historyTask = _sessionManager.GetConversationHistoryAsync(sessionId);
            var profileTask = _providerRepo.GetByIdAsync(providerId);

            await Task.WhenAll(historyTask, profileTask).ConfigureAwait(false);
            
            var history = (await historyTask).ToList();
            if (history.Count > 0 && history.Last().Role == "user")
            {
                var last = history[history.Count - 1];
                var personaSuffix = _settingsService.DeveloperPersonaEnabled && !string.IsNullOrWhiteSpace(_settingsService.DeveloperPersona) 
                    ? $"\n\n[USER PERSONA / CONTEXT FOR YOU TO CONSIDER]\n{_settingsService.DeveloperPersona}"
                    : "";
                var appendedContent = personaSuffix + (answerContext ?? "");
                history[history.Count - 1] = last with { Content = userMessage + appendedContent };
                userMessage += appendedContent;
            }
            // 2a. Resolve provider settings before the request pipeline chooses its adapter.
            var modelId = ActiveSession?.ModelId ?? "gpt-4o";

            string apiKey = "";
            string? apiEndpoint = null;
            int maxContextTokens = 1000000;
            string providerType = "openai";

            var profile = await profileTask;
            if (profile != null)
            {
                apiKey = profile.ApiKey ?? "";
                apiEndpoint = profile.ApiEndpoint;
                maxContextTokens = profile.ContextWindowTokens;
                providerType = !string.IsNullOrEmpty(profile.ProviderType) ? profile.ProviderType : "openai";
            }

            var tools = _toolRegistry.GetTools().Cast<AITool>().ToList();


            
            // In the new system, EnhancedAgentModePolicy handles prompt injection, and the new tools
            // (ProposePlanTool, etc.) are standard DI tools. We bypass the legacy inline injection.
            if (AgentModes.IsPlan(turnMode) && !_settingsService.UseEnhancedPlanSystem)
            {
                // Remove every always-mutating tool from the request. The model cannot call what it
                // was never offered, so this — not prompt wording — is what makes Plan mode read-only.
                var removed = tools.Where(tool => AgentModePolicy.IsBlockedInPlanMode(tool.Name))
                                   .Select(tool => tool.Name)
                                   .ToList();
                tools.RemoveAll(tool => AgentModePolicy.IsBlockedInPlanMode(tool.Name));

                // Tools that only mutate for certain arguments stay available behind a guard, so
                // planning a database change can still inspect data without being able to change it.
                for (int i = 0; i < tools.Count; i++)
                {
                    if (tools[i] is AIFunction function && AgentModePolicy.NeedsReadOnlyGuardInPlanMode(function.Name))
                    {
                        if (string.Equals(function.Name, "execute_query", StringComparison.OrdinalIgnoreCase))
                        {
                            tools[i] = new ReadOnlyGuardAIFunction(
                                function,
                                "Blocked: PLAN MODE is read-only, so only SELECT/WITH queries are permitted. " +
                                "Describe the data change as a step in your plan instead, and run it after the user approves the plan in ACT MODE.",
                                "queryString", "query", "sql");
                            removed.Add(function.Name + " (read-only)");
                        }
                        else if (string.Equals(function.Name, "execute_command", StringComparison.OrdinalIgnoreCase))
                        {
                            tools[i] = new ReadOnlyCommandGuardAIFunction(
                                function,
                                "Blocked: PLAN MODE is read-only. File modification is not allowed in the plan. Only non-modifying commands (like git grep, dir, cat, etc.) are permitted.",
                                "command");
                            removed.Add(function.Name + " (read-only)");
                        }
                    }
                }

                if (removed.Count > 0)
                {
                    _outputLogger.Log(LogCategory.Agent, $"Plan mode tool gating → restricted: {string.Join(", ", removed)}");
                }

                var planSchema = @"
                {
                  ""type"": ""object"",
                  ""properties"": {
                    ""steps"": {
                      ""type"": ""array"",
                      ""description"": ""The ordered list of steps in the plan."",
                      ""items"": {
                        ""type"": ""object"",
                        ""properties"": {
                          ""title"": { ""type"": ""string"", ""description"": ""Short imperative summary of the step."" },
                          ""description"": { ""type"": ""string"", ""description"": ""What will change and where: name the concrete files, types and members involved."" },
                          ""toolsRequired"": {
                            ""type"": ""array"",
                            ""items"": { ""type"": ""string"" },
                            ""description"": ""Optional list of tool names (e.g. 'delete_file', 'replace_in_file') needed for this step.""
                          },
                          ""requiresConfirmation"": {
                            ""type"": ""boolean"",
                            ""description"": ""Set to true if this step performs a destructive action (like delete_file or execute_command) that the user should explicitly review before execution.""
                          }
                        },
                        ""required"": [""title"", ""description""]
                      }
                    }
                  },
                  ""required"": [""steps""]
                }";

                var planTool = new InlineCustomAIFunction(
                    AgentModePolicy.PlanToolName,
                    "Presents a structured implementation plan to the user for review. Call this once you have gathered enough information to commit to an approach. The user can approve the plan or return per-step feedback for you to revise. Stop after calling it and wait for the response.",
                    planSchema,
                    args => 
                    {
                        try
                        {
                            if (args.TryGetValue("steps", out var stepsObj) && stepsObj != null)
                            {
                                var stepsJson = System.Text.Json.JsonSerializer.Serialize(stepsObj);
                                var steps = System.Text.Json.JsonSerializer.Deserialize<List<PlanItem>>(
                                    stepsJson, 
                                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                                // Drop empty rows and any Comment the model tried to author: the
                                // comment channel belongs to the reviewer, not to the model.
                                var sanitized = steps?
                                    .Where(step => step != null &&
                                                   (!string.IsNullOrWhiteSpace(step.Title) || !string.IsNullOrWhiteSpace(step.Description)))
                                    .Select(step => new PlanItem
                                    {
                                        Title = string.IsNullOrWhiteSpace(step.Title) ? "Step" : step.Title.Trim(),
                                        Description = step.Description?.Trim() ?? string.Empty,
                                        ToolsRequired = step.ToolsRequired,
                                        RequiresConfirmation = step.RequiresConfirmation,
                                        Comment = null,
                                    })
                                    .ToList();

                                if (sanitized != null && sanitized.Count > 0)
                                {


                                    _outputLogger.Log(LogCategory.Agent, $"Plan proposed with {sanitized.Count} step(s)");
                                    PlanProposed?.Invoke(this, sanitized);
                                }
                                else
                                {
                                    _logger.LogWarning("plan_mode_respond was called with no usable steps.");
                                    return Task.FromResult<object?>(
                                        "Error: no usable steps were supplied. Call the tool again with a non-empty 'steps' array where each entry has a title and a description.");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to parse plan steps.");
                            return Task.FromResult<object?>(
                                $"Error: the plan could not be parsed ({ex.Message}). Call the tool again with a 'steps' array of objects that each have a 'title' and a 'description'.");
                        }
                        return Task.FromResult<object?>(
                            "Plan submitted for user review. Stop here and wait: the user will either approve the plan or send per-step feedback for you to revise. Do not call any more tools in this turn.");
                    }
                );
                tools.Add(planTool);
            }
            else if (_settingsService.UseEnhancedPlanSystem)
            {
                activePlan = await _planService.GetActivePlanAsync(sessionId, ct);
                var status = activePlan?.Status ?? PlanStatus.Draft;
                
                // If user explicitly selected Act mode (turnMode is Act) and the plan is in a planning or completed state,
                // bypass the Enhanced Plan System instruction injection to allow direct execution.
                bool bypassPlanSystem = !AgentModes.IsPlan(turnMode) && 
                    (activePlan == null || 
                     status == PlanStatus.Draft || 
                     status == PlanStatus.AwaitingReview || 
                     status == PlanStatus.RevisionRequested || 
                     status == PlanStatus.Completed ||
                     status == PlanStatus.Superseded);

                if (!bypassPlanSystem)
                {
                    if (history.Count > 0 && history.Last().Role == "user")
                    {
                        var last = history[history.Count - 1];
                        var instruction = EnhancedAgentModePolicy.BuildInstruction(status);
                        history[history.Count - 1] = last with { Content = last.Content + instruction };
                        userMessage += instruction;
                    }

                    var toolNames = tools.Select(t => t.Name).ToList();
                    var removed = await _toolGatingService.FilterToolsAsync(sessionId, toolNames);
                    tools.RemoveAll(t => removed.Contains(t.Name));
                    
                    bool isReadOnlyPhase = status != PlanStatus.TasksGenerated && status != PlanStatus.InProgress;

                    for (int i = 0; i < tools.Count; i++)
                    {
                        if (isReadOnlyPhase && tools[i] is AIFunction function && AgentModePolicy.NeedsReadOnlyGuardInPlanMode(function.Name))
                        {
                            if (string.Equals(function.Name, "execute_query", StringComparison.OrdinalIgnoreCase))
                            {
                                tools[i] = new ReadOnlyGuardAIFunction(
                                    function,
                                    "Blocked: Current phase is read-only, so only SELECT/WITH queries are permitted.",
                                    "queryString", "query", "sql");
                                removed.Add(function.Name + " (read-only)");
                            }
                            else if (string.Equals(function.Name, "execute_command", StringComparison.OrdinalIgnoreCase))
                            {
                                tools[i] = new ReadOnlyCommandGuardAIFunction(
                                    function,
                                    "Blocked: Current phase is read-only. File modification is not allowed. Only non-modifying commands (like git grep, dir, cat, etc.) are permitted.",
                                    "command");
                                removed.Add(function.Name + " (read-only)");
                            }
                        }
                    }

                    if (removed.Count > 0)
                    {
                        _outputLogger.Log(LogCategory.Agent, $"Enhanced Plan tool gating (Status: {status}) → restricted: {string.Join(", ", removed)}");
                    }
                }
            }

            if (AgentModes.IsPlan(turnMode))
                tools.RemoveAll(t => t.Name == "attempt_completion");
            bool requiresCompletion = !AgentModes.IsPlan(turnMode) && tools.Any(t => t.Name == "attempt_completion");

            int maxOutputTokens = 8192;
            if (providerId.Equals("openrouter", StringComparison.OrdinalIgnoreCase))
            {
                maxOutputTokens = 131072;
            }
            else if (providerId.Equals("openai", StringComparison.OrdinalIgnoreCase) || providerId.Equals("azure", StringComparison.OrdinalIgnoreCase))
            {
                maxOutputTokens = modelId.StartsWith("gpt-4o", StringComparison.OrdinalIgnoreCase) ? 16384 : 4096;
            }
            else if (providerId.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
            {
                maxOutputTokens = 8192;
            }

            var options = new ChatOptions 
            {
                MaxOutputTokens = maxOutputTokens 
            };
            if (tools.Count > 0)
            {
                var fallbackSchema = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":true}";
                tools.Add(new InlineCustomAIFunction(
                    "invalid_tool_call_handler",
                    "Fallback for invalid tool calls. Do not use this tool manually.",
                    fallbackSchema,
                    args => Task.FromResult<object?>("invalid tool call: name required or unparseable. Please emit a valid tool call or respond to the user directly.")
                ));
                options.Tools = tools;
            }

            var fileContextBudgetChars = await GetFileContextBudgetCharsAsync().ConfigureAwait(false);
            
            // Ephemeral Context Injection
            var fileContextBlock = await BuildEphemeralFileContextAsync(history, fileContextBudgetChars).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(fileContextBlock))
            {
                userMessage = fileContextBlock + "\n<user_request>\n" + userMessage + "\n</user_request>";
            }
            else
            {
                userMessage = "<user_request>\n" + userMessage + "\n</user_request>";
            }

            // The current user turn is supplied separately to provider adapters. Persisted history
            // remains pure user/assistant data and never contains a system-role message.
            // Legacy Migration: strip baked-in <file_context> from history.
            var adapterHistory = history
                .Take(Math.Max(0, history.Count - 1))
                .Where(turn => turn.Role == "user" || turn.Role == "assistant")
                .Select(turn => 
                {
                    var cleanContent = turn.Content;
                    if (turn.Role == "user" && cleanContent.Contains("<file_context"))
                    {
                        cleanContent = System.Text.RegularExpressions.Regex.Replace(cleanContent, @"<file_context[^>]*>.*?</file_context>\s*", "", System.Text.RegularExpressions.RegexOptions.Singleline);
                    }
                    return new ChatMessage(turn.Id, turn.Role, cleanContent, turn.Timestamp, OriginalPrompt: turn.OriginalPrompt, Metadata: turn.Metadata);
                })
                .ToArray();
            
            var preparedRequest = await _chatRequestPipeline
                .SendAsync(providerType, adapterHistory, userMessage, options, turnMode, fileContextBudgetChars, userTurn.Metadata, ct)
                .ConfigureAwait(false);
            var systemPrompt = preparedRequest.SystemPrompt + "\nCommand verification: execute_command may return status=running. Continue read_command_output with next_cursor until a foreground command exits and has_more=false. Never rerun a running command. Only status=completed with exit_code=0 and complete diagnostics supports a successful build. Inspect log_path if truncated or capture_error is present. Use background=true only for persistent servers such as ng serve; verify readiness separately, report the retained session, and stop it when no longer needed. Commands are stopped when the turn is cancelled or the workspace changes.";
            if (requiresCompletion)
                systemPrompt += "\nACT completion protocol: Continue using tools until the user's request is fulfilled. " +
                    "Text alone is commentary and does not finish the turn. After receiving all tool results and performing appropriate verification, " +
                    "call attempt_completion alone with the final summary and actual verification results. " +
                    "Do not claim checks passed unless their results support that claim. Complete any active plan tasks first; " +
                    "a plan is not required for a direct task. If blocked or missing required input, use ask_question. " +
                    "Do not create a plan or invent a task ID merely to finish.";
            _outputLogger.Log(LogCategory.Llm, $"System prompt prepared ({systemPrompt.Length} chars, context {preparedRequest.Context.StableHash})", systemPrompt);

            // 2b. Context Compaction & Token Budgeting
            var systemPromptTokens = ContextCompactor.EstimateTokens(systemPrompt);
            var toolsJson = tools.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(
                tools.Select(tool => new { name = tool.Name, description = tool.Description, parameters = (tool as AIFunction)?.JsonSchema })) : "";
            var toolsTokens = ContextCompactor.EstimateTokens(toolsJson);

            var compactionResult = ContextCompactor.ApplyEvictionPolicy(
                history,
                systemPromptTokens,
                toolsTokens,
                providerId,
                modelId,
                maxContextTokens);

            int totalTokens = compactionResult.TotalTokens;
            int historyBudget = compactionResult.HistoryBudget;

            // Pre-flight Check: Hard refusal if still over budget
            if (compactionResult.IsOverBudget)
            {
                var errorMsg = $"Your request requires {totalTokens} tokens, which exceeds this model's budget of {historyBudget} tokens. Please shorten your request or switch models.";
                Escalate(errorMsg);
                return; // Fail-fast
            }

            // Report total tokens
            // Report the assembled outbound request below, after history repair.

            // Declare userMessageId at broader scope so it's accessible for post-tool checkpoint
            var userMessageId = userTurn.Id;

            // Checkpoint before AI starts if we have tools
            if (_checkpointService.IsAvailable && tools.Count > 0)
            {
                var ckptId = await _checkpointService.SaveCheckpointAsync(sessionId, userMessageId, "Before AI Turn", ct);
                if (ckptId != null)
                {
                    var cpInfos = await _checkpointService.GetCheckpointsForSessionAsync(sessionId);
                    var cp = cpInfos.FirstOrDefault(c => c.Id == ckptId);
                    if (cp != null) CheckpointCreated?.Invoke(this, cp);
                }
            }

            // 3. Build MEAI chat messages
            var chatMessages = new List<Microsoft.Extensions.AI.ChatMessage>
            {
                new(ChatRole.System, systemPrompt)
            };
            foreach (var turn in history)
            {
                if (!string.IsNullOrWhiteSpace(turn.SerializedContentBlocks))
                {
                    try
                    {
                        var blocks = System.Text.Json.JsonSerializer.Deserialize<List<ContentBlock>>(turn.SerializedContentBlocks);
                        if (blocks != null && blocks.Count > 0)
                        {
                            var currentRole = blocks[0].Role;
                            var currentContents = new List<AIContent>();
                            
                            foreach (var block in blocks)
                            {
                                if (block.Role != currentRole)
                                {
                                    chatMessages.Add(new Microsoft.Extensions.AI.ChatMessage(new ChatRole(currentRole), currentContents));
                                    currentRole = block.Role;
                                    currentContents = new List<AIContent>();
                                }

                                if (block.Text != null) currentContents.Add(new TextContent(block.Text));
                                else if (block.Name != null) 
                                {
                                    var normalizedArgs = block.Arguments != null
                                        ? block.Arguments.ToDictionary(kvp => kvp.Key, kvp => NormalizeJsonElement(kvp.Value))
                                        : new Dictionary<string, object?>();
                                    currentContents.Add(new FunctionCallContent(block.CallId ?? "", block.Name, normalizedArgs));
                                }
                                else if (block.Result != null) currentContents.Add(new FunctionResultContent(block.CallId ?? "", block.Result));
                            }
                            if (currentContents.Count > 0)
                            {
                                chatMessages.Add(new Microsoft.Extensions.AI.ChatMessage(new ChatRole(currentRole), currentContents));
                            }
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to deserialize content blocks for turn {TurnId}", turn.Id);
                    }
                }

                // FIX #4: Default unknown roles to User, NEVER System.
                // The System prompt is prepended above (L144) and must never appear mid-history.
                // A corrupt or unexpected role value in the DB would previously crash Anthropic/strict
                // OpenAI endpoints with a 400 Bad Request.
                var role = turn.Role == "user" ? ChatRole.User :
                           turn.Role == "assistant" ? ChatRole.Assistant : ChatRole.User;

                // FIX (Context missing): The raw DB turn lacks ephemeral file context and the
                // volatile state block. The pipeline (preparedRequest.UserMessage) contains the
                // fully augmented text for the final turn. Swap it in!
                string contentToUse = turn.Content;
                if (role == ChatRole.User && turn == history.Last())
                {
                    contentToUse = preparedRequest.UserMessage;
                }

                chatMessages.Add(new Microsoft.Extensions.AI.ChatMessage(role, contentToUse));
            }

            ChatTurnRecovery.RepairToolPairs(chatMessages);

            // 4. Get LLM client. Microsoft.Extensions.AI performs the wire send; the adapter
            // payload is logged to verify provider-specific system-prompt placement.
            var client = _clientFactory.CreateClient(providerType, modelId, apiKey, apiEndpoint);
            try
            {
                _outputLogger.Log(LogCategory.Llm, $"▼ Request → {providerType}/{modelId} ({chatMessages.Count} msgs)");
            }
            catch { /* Ignore */ }

            var currentMessages = new List<Microsoft.Extensions.AI.ChatMessage>(chatMessages);
            bool loopAgent = true;

            // Create a single conversation-level transaction that captures the entire
            // agentic loop (all tool-call iterations) as one logical request/response.
            // This prevents the Raw tab from showing a separate entry for every LLM API call
            // within the loop — the user sees one request/response per conversation turn.
            conversationTransaction = new LlmTransaction(providerId, modelId, JsonSerializer.Serialize(new { Messages = chatMessages, Options = options }, new JsonSerializerOptions { WriteIndented = true }));
            _logBus.Publish(conversationTransaction);

            // Set the suppression flag so DiagnosticsLoggingChatClient does not create
            // per-call transactions. The conversation-level transaction above is the only one.
            prevSuppressedTx = AiAssistant.Llm.Services.DiagnosticsLoggingChatClient.ActiveConversationTransaction.Value;
            AiAssistant.Llm.Services.DiagnosticsLoggingChatClient.ActiveConversationTransaction.Value = conversationTransaction;
            hadSuppressedTx = prevSuppressedTx != null;

            conversationStopwatch.Restart();

            // 5. Tool execution & streaming
            assistantMessage = new ChatMessage(Guid.NewGuid().ToString(), "assistant", "", DateTime.UtcNow, true);
            MessageReceived?.Invoke(this, assistantMessage);

            // Declare didToolUse outside loop so it's accessible after loop ends
            bool didToolUse = false;

            while (loopAgent && !ct.IsCancellationRequested)
            {
                if (!resilience.TryRequest())
                {
                    Escalate($"The model request limit ({resilience.Options.MaxModelRequests}) was reached.");
                    break;
                }
                finalResponse = null;
                loopAgent = false;
                accumulatedContent = string.Empty;


                wasCancelled = false;
                var seenCallIds = new HashSet<string>();

                // Declare streaming variables at outer scope so they're accessible in catch blocks
                var updates = new List<Microsoft.Extensions.AI.ChatResponseUpdate>();
                var sb = new System.Text.StringBuilder();

                void AddFeedback(string code, string detail)
                {
                    var feedback = new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, ChatTurnRecovery.Feedback(code, detail));
                    currentMessages.Add(feedback);
                    allMessagesIncludingTools.Add(feedback);
                    _logger.LogInformation("Agent recovery {Code} for session {SessionId}", code, sessionId);
                }

                // Include injected files, tool results, system prompt and recovery messages.
                int requestTokens = toolsTokens + currentMessages.Sum(m => EstimateMessageTokens(m) + 4);
                _contextUsage[sessionId] = (requestTokens, maxContextTokens);
                TokenUsageChanged?.Invoke(this, (requestTokens, maxContextTokens));
                try
                {
                    // Use the raw factory client: this loop owns retries and function dispatch.
                    // A partial generation never executes a function, so replay cannot duplicate effects.
                    if (providerType.Equals("ollama", StringComparison.OrdinalIgnoreCase) && tools.Count > 0)
                    {
                        finalResponse = await ChatTurnRecovery.WithCancellation(
                            client.GetResponseAsync(currentMessages, options, ct), ct).ConfigureAwait(false);
                        accumulatedContent = finalResponse.Text ?? "";
                    }
                    else
                    {
                        using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var enumerator = client.GetStreamingResponseAsync(currentMessages, options, streamCancellation.Token)
                            .GetAsyncEnumerator(streamCancellation.Token);
                        Task<bool>? pending = null;
                        try
                        {
                            while (true)
                            {
                                streamCancellation.CancelAfter(resilience.Options.StreamIdleTimeout);
                                pending = enumerator.MoveNextAsync().AsTask();
                                bool hasNext;
                                try
                                {
                                    hasNext = await ChatTurnRecovery.WithCancellation(pending, streamCancellation.Token).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                                {
                                    throw new TimeoutException("The provider stopped delivering stream updates.");
                                }
                                if (!hasNext) break;
                                ProcessStreamingUpdate(enumerator.Current, sb, updates, ref accumulatedContent, ref assistantMessage, seenCallIds);
                            }
                            finalResponse = updates.ToChatResponse();
                        }
                        finally
                        {
                            streamCancellation.Cancel();
                            if (pending == null || pending.IsCompleted)
                                await ChatTurnRecovery.WithCancellation(DisposeStreamAsync(enumerator), ct).ConfigureAwait(false);
                            else
                            {
                                // An uncooperative provider must not keep the UI turn alive. Dispose only
                                // after its outstanding read finishes, and observe cleanup failures.
                                _ = pending.ContinueWith(async task =>
                                {
                                    _ = task.Exception;
                                    try { await enumerator.DisposeAsync().ConfigureAwait(false); }
                                    catch (Exception cleanupError) { _logger.LogDebug(cleanupError, "Deferred stream cleanup failed"); }
                                }, TaskScheduler.Default).Unwrap();
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    if (callerToken.IsCancellationRequested) wasCancelled = true;
                    else Escalate("The agent took too long to complete the task and the turn timeout was reached. Some tool operations may have been aborted.", AgentPauseReason.Timeout);
                    break;
                }
                catch (Exception ex)
                {
                    if (LlmErrorClassifier.IsContextWindowError(ex))
                    {
                        if (!resilience.TryRetry("context", resilience.Options.MaxContextRetries, out _) ||
                            !ChatTurnRecovery.TrimHistory(currentMessages))
                        {
                            Escalate("The request still exceeds the context window and cannot be reduced safely.");
                            break;
                        }
                        AddFeedback("context_truncated", "Older complete conversation turns were removed. The initial task and latest turn remain. Continue from the retained history.");
                        loopAgent = true;
                        continue;
                    }

                    var rateLimit = IsRateLimitError(ex) && !IsAuthError(ex);
                    var retryable = rateLimit || ChatTurnRecovery.IsTransient(ex);
                    var kind = rateLimit ? "rate_limit" : "transport";
                    var limit = rateLimit ? resilience.Options.MaxRateLimitRetries : resilience.Options.MaxTransportRetries;
                    if (!retryable || !resilience.TryRetry(kind, limit, out var attempt))
                    {
                        Escalate(retryable ? $"The provider's {kind} retry limit was reached." :
                            "The provider rejected the request: " + ex.Message);
                        break;
                    }
                    _logger.LogWarning(ex, "Retrying {Kind} for session {SessionId}", kind, sessionId);
                    var delay = resilience.RetryDelay(ex, attempt);
                    RetryInitiated?.Invoke(this, (delay, kind));
                    _statusBarService.SetText($"Retrying {kind} in {delay.TotalSeconds:F1}s ({attempt}/{limit})...");
                    // Discard only this incomplete generation. Previous completed tool results remain.
                    AddFeedback(kind, "Generation was interrupted; no tools from that incomplete response were executed. Produce a fresh complete response.");
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    loopAgent = true;
                    continue;
                }

                ct.ThrowIfCancellationRequested();
                // Display the latest request, not cumulative usage across the agent loop.
                // MEAI normalizes cached input into InputTokenCount; do not add it twice.
                var usage = finalResponse?.Usage;
                long inputTokens = usage?.InputTokenCount ?? requestTokens;
                long outputTokens = usage?.OutputTokenCount ?? (finalResponse?.Messages.Sum(m => EstimateMessageTokens(m) + 4) ?? 0);
                long contextTokens = usage?.TotalTokenCount ?? (inputTokens + outputTokens);
                var latestUsage = ((int)Math.Min(int.MaxValue, Math.Max(0, contextTokens)), maxContextTokens);
                _contextUsage[sessionId] = latestUsage;
                TokenUsageChanged?.Invoke(this, latestUsage);
                accumulatedContent = finalResponse?.Text ?? sb.ToString();
                assistantMessage = assistantMessage with { Content = accumulatedContent };
                StreamingUpdated?.Invoke(this, assistantMessage);
                var knownToolNames = tools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                // Tool examples inside fenced code are data, not executable requests.
                var toolText = Regex.Replace(accumulatedContent, @"```[\s\S]*?(?:```|$)", "");
                var parsed = AssistantMessageParser.Parse(toolText, knownToolNames);
                var partialXml = parsed.Any(b => b.IsToolCall && b.ToolCall?.Partial == true);
                var xmlCalls = AssistantMessageParser.ExtractCompleteToolCalls(toolText, knownToolNames);
                var nativeCalls = finalResponse?.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList()
                    ?? new List<FunctionCallContent>();
                if (finalResponse?.FinishReason == ChatFinishReason.Length || nativeCalls.Any(c => c.Exception != null ||
                    string.IsNullOrWhiteSpace(c.Name) || string.IsNullOrWhiteSpace(c.CallId)) ||
                    nativeCalls.Select(c => c.CallId).Distinct(StringComparer.Ordinal).Count() != nativeCalls.Count)
                {
                    AddFeedback("incomplete_response", "The response was truncated or contained invalid native tool arguments/IDs. No tools were executed. Produce a smaller, complete response with valid tool arguments and unique call IDs.");
                    if (!resilience.RecordMistake()) { Escalate("The model repeatedly produced incomplete responses."); break; }
                    loopAgent = true;
                    continue;
                }
                var calls = new List<FunctionCallContent>(nativeCalls);
                // Native calls are authoritative if both representations are returned; never execute twice.
                if (nativeCalls.Count == 0 && !partialXml)
                {
                    foreach (var xml in xmlCalls)
                    {
                        var function = tools.OfType<AIFunction>().FirstOrDefault(f => f.Name == xml.Name);
                        var arguments = new Dictionary<string, object?>();
                        foreach (var parameter in xml.Params)
                        {
                            object? value = parameter.Value;
                            // XML wraps strings directly and structured/scalar non-strings as JSON.
                            if (function?.JsonSchema.TryGetProperty("properties", out var properties) == true &&
                                properties.TryGetProperty(parameter.Key, out var schema) &&
                                schema.TryGetProperty("type", out var type) && type.GetRawText() != "\"string\"")
                            {
                                try { value = JsonSerializer.Deserialize<JsonElement>(parameter.Value); }
                                catch (JsonException) { /* Validation below supplies model feedback. */ }
                            }
                            arguments[parameter.Key] = value;
                        }
                        calls.Add(new FunctionCallContent(xml.CallId, xml.Name, arguments));
                    }
                }

                var textOnly = xmlCalls.Count > 0 ? AssistantMessageParser.ExtractTextOnly(toolText, knownToolNames) : accumulatedContent;
                var proseWithoutCode = Regex.Replace(textOnly, @"```[\s\S]*?```", "");
                bool invalidXml = nativeCalls.Count == 0 && (partialXml || AssistantMessageParser.ContainsUnknownXmlTags(proseWithoutCode) ||
                    Regex.IsMatch(proseWithoutCode, @"<[A-Za-z_][A-Za-z_0-9]*$"));
                if (invalidXml)
                {
                    AddFeedback("invalid_tool_xml", "The completed response contains an unknown tool or incomplete/mismatched XML tags. No XML calls in this response were executed. Use a declared native tool with all required parameters, or emit a complete declared XML tool call.");
                    if (!resilience.RecordMistake()) { Escalate("The model repeatedly produced invalid tool XML."); break; }
                    loopAgent = true;
                    continue;
                }

                if (calls.Count == 0)
                {
                    if (string.IsNullOrWhiteSpace(accumulatedContent))
                    {
                        if (!resilience.TryRetry("empty", resilience.Options.MaxEmptyRetries, out var attempt))
                        { Escalate("The model repeatedly returned empty responses."); break; }
                        AddFeedback("empty_response", "Failure: I did not provide a response. Respond with meaningful text or a valid tool call.");
                        var delay = resilience.RetryDelay(null, attempt);
                        RetryInitiated?.Invoke(this, (delay, "Empty response"));
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        // Preserve provider-specific reasoning/content metadata in valid responses.
                        var responseMessages = finalResponse!.Messages;
                        currentMessages.AddRange(responseMessages);
                        allMessagesIncludingTools.AddRange(responseMessages);
                        // ACT requires completion only when its dedicated completion tool is offered.
                        // Question/plan tools alone must never prevent natural text completion.
                        if (!requiresCompletion) break;
                        AddFeedback("stopped_before_completion", "Continue the task using a declared tool. Finish or request user input using the appropriate available terminal tool: " +
                            string.Join(", ", tools.Where(t => IsTerminalTool(t.Name)).Select(t => t.Name)) + ". Do not merely describe the next action.");
                        if (!resilience.RecordMistake()) { Escalate("The agent stalled without an accepted completion. Progress is preserved, but completion has not been confirmed."); break; }
                    }
                    loopAgent = true;
                    continue;
                }

                // One authoritative assistant call batch, followed by exactly one result per call.
                var responseContents = new List<AIContent>();
                if (!string.IsNullOrWhiteSpace(textOnly)) responseContents.Add(new TextContent(textOnly));
                responseContents.AddRange(calls);
                if (nativeCalls.Count > 0)
                {
                    currentMessages.AddRange(finalResponse!.Messages);
                    allMessagesIncludingTools.AddRange(finalResponse.Messages);
                }
                else
                {
                    var callMessage = new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, responseContents);
                    currentMessages.Add(callMessage);
                    allMessagesIncludingTools.Add(callMessage);
                }
                bool terminal = false;
                bool stop = false;
                bool batchFailed = false;
                var results = new List<AIContent>();
                foreach (var call in calls)
                {
                    object? result;
                    var function = tools.OfType<AIFunction>().FirstOrDefault(f => string.Equals(f.Name, call.Name, StringComparison.Ordinal));
                    var arguments = new AIFunctionArguments(call.Arguments ?? new Dictionary<string, object?>());
                    string? error = terminal || stop ? "Skipped because the turn has already stopped. Do not execute this action." :
                        function == null ? $"Unknown or unavailable tool '{call.Name}'. Use only the tools declared in this request." :
                        ChatTurnRecovery.ValidateArguments(function, arguments) ?? resilience.CheckToolCall(call.Name, arguments);
                    if (error == null && call.Name == "attempt_completion")
                    {
                        if (_commandSessions?.HasPendingBuilds(sessionId) == true)
                            error = "A foreground command is still running or has unread final output. Read its output and exit code before completing the task. Session IDs: " + _commandSessions.PendingSessionIds(sessionId);
                        else if (calls.Count != 1)
                            error = "Call attempt_completion alone in a separate response after reviewing all other tool results.";
                        else
                        {
                            var completionPlan = await _planService.GetActivePlanAsync(sessionId, ct).ConfigureAwait(false);
                            if (completionPlan != null && completionPlan.Tasks.Any(t => t.Status != AiAssistant.Core.Models.TaskStatus.Completed))
                                error = "The active plan has unfinished tasks. Finish them before attempting completion, or use ask_question if blocked.";
                        }
                    }
                    if (error != null)
                    {
                        _outputLogger.Log(LogCategory.Agent, $"Tool validation failed for {call.Name}", detail: error);
                        result = ChatTurnRecovery.Feedback("invalid_tool_call", error);
                        if (!terminal && !stop) { batchFailed = true; lastToolError = call.Name + ": " + error; }
                    }
                    else
                    {
                        try
                        {
                            ct.ThrowIfCancellationRequested();
                            using var explorerDeferral = _vsEnvService.DeferSolutionExplorerRefresh();
                            CommandSessionService.CallId.Value = call.CallId;
                            var toolInfo = new ToolCallInfo(call.CallId, call.Name, call.Arguments);
                            ToolExecuting?.Invoke(this, toolInfo);
                            _outputLogger.Log(LogCategory.Tool, $"Executing {call.Name}", detail: JsonSerializer.Serialize(call.Arguments));
                            result = await ChatTurnRecovery.WithCancellation(function!.InvokeAsync(arguments, ct).AsTask(), ct).ConfigureAwait(false);
                            didToolUse = true;
                            var resultText = result?.ToString() ?? "";
                            if (IsFailedToolResult(resultText) && !IsCommandProgressResult(call.Name, resultText))
                            {
                                // Command results carry structured exit status and output cursors. Preserve
                                // them so the model can drain diagnostics and the terminal can render them.
                                if (call.Name != "execute_command" && call.Name != "read_command_output" && call.Name != "stop_command")
                                    result = ChatTurnRecovery.Feedback("tool_failed", resultText);
                                batchFailed = true;
                                toolPauseReason = AgentPauseReason.ToolFailure;
                                lastToolError = call.Name + ": " + resultText;
                            }
                            else
                            {
                                // Reset the mistake budget only after a clean batch.
                                terminal = IsTerminalTool(call.Name);
                                if (call.Name == "attempt_completion")
                                {
                                    acceptedCompletionCallId = call.CallId;
                                    accumulatedContent = resultText;
                                    assistantMessage = assistantMessage with { Content = resultText };
                                    // This is a replacement, not a streaming text suffix. Publish it
                                    // with the authoritative blocks at turn completion below.
                                }
                            }
                            ToolCompleted?.Invoke(this, new ToolResultInfo(call.CallId, result?.ToString() ?? ""));
                            if (call.Name == "create_file" || call.Name == "delete_file" ||
                                call.Name == "rename_file" || call.Name == "replace_in_file" ||
                                call.Name == "execute_command" || call.Name == "revert_transaction")
                                await RefreshExplorerAsync().ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            result = ChatTurnRecovery.Feedback("interrupted_tool", "The tool was cancelled or timed out; its outcome is unknown. Inspect state before retrying.");
                            stop = true;
                            toolPauseReason = AgentPauseReason.Timeout;
                            lastToolError = call.Name + ": Operation interrupted; inspect state before retrying.";
                            wasCancelled = callerToken.IsCancellationRequested;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Tool {Tool} failed", call.Name);
                            result = ChatTurnRecovery.Feedback("tool_failed", ex.Message + " The action may have partially completed. Inspect current state before trying again.");
                            batchFailed = true;
                            toolPauseReason = AgentPauseReason.ToolFailure;
                            lastToolError = call.Name + ": " + ex.Message;
                        }
                    }
                    results.Add(new FunctionResultContent(call.CallId, result));
                }
                // One failed model response consumes one correction opportunity.
                if (!stop && batchFailed) stop = !resilience.RecordMistake();
                else if (!stop) resilience.RecordSuccess();
                var resultMessage = new Microsoft.Extensions.AI.ChatMessage(ChatRole.Tool, results);
                currentMessages.Add(resultMessage);
                allMessagesIncludingTools.Add(resultMessage);
                if (terminal && calls.Count == 1 && calls[0].Name == "attempt_completion")
                {
                    var completionMessage = new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, accumulatedContent);
                    acceptedCompletionMessage = completionMessage;
                    currentMessages.Add(completionMessage);
                    allMessagesIncludingTools.Add(completionMessage);
                }
                if (stop)
                {
                    if (!wasCancelled) Escalate(toolPauseReason == AgentPauseReason.Timeout ? "The turn timed out. Inspect state before retrying." : toolPauseReason == AgentPauseReason.InvalidToolArguments ? "Repeated invalid tool arguments. Completed work is preserved." : "Repeated tool execution errors. Inspect state before retrying.", toolPauseReason);
                    break;
                }
                if (terminal) break;
                loopAgent = true;
        } // End of while loop

        // Checkpoint after tool execution so the task can be resumed
        if (didToolUse && _checkpointService.IsAvailable)
        {
            try
            {
                await _checkpointService.SaveCheckpointAsync(sessionId, userMessageId, "After Tool Execution", ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save post-tool checkpoint for session {SessionId}", sessionId);
            }
        }

        // 6. Finalize
        // Accepted completions publish after persistence, with their structured trace.
        if (acceptedCompletionCallId == null) FireStreamingCompleted(wasCancelled);

        // 7. Store assistant message
            string serializedBlocks = null;
            try
            {
                var storedContents = new List<ContentBlock>();
                
                // FIX Feature 2 & 5: ChatResponse.Messages contains ONLY the messages generated during
                // this response (assistant tool-call message(s), tool-result message(s), and the final
                // assistant text) — the input system prompt + history are NOT included. Serialize every
                // message from index 0 so the full tool-call/tool-result sequence round-trips through
                // history persistence and is re-hydrated into the LLM context on the next turn.
                if (allMessagesIncludingTools != null && allMessagesIncludingTools.Count > 0)
                {
                    foreach (var msg in allMessagesIncludingTools)
                    {
                        foreach (var content in msg.Contents)
                        {
                            if (content is TextContent tc)
                            {
                                // Prose accompanying the accepted call is replaced by its canonical answer.
                                if (acceptedCompletionCallId != null && msg.Contents.OfType<FunctionCallContent>()
                                    .Any(c => c.CallId == acceptedCompletionCallId)) continue;
                                storedContents.Add(new ContentBlock { Role = msg.Role.Value, Text = tc.Text,
                                    CompletionCallId = ReferenceEquals(msg, acceptedCompletionMessage) ? acceptedCompletionCallId : null });
                            }
                            else if (content is FunctionCallContent fcc)
                            {
                                storedContents.Add(new ContentBlock { Role = msg.Role.Value, CallId = fcc.CallId, Name = fcc.Name, Arguments = fcc.Arguments });
                            }
                            else if (content is FunctionResultContent frc)
                            {
                                storedContents.Add(new ContentBlock { Role = msg.Role.Value, CallId = frc.CallId, Result = frc.Result?.ToString() });
                            }
                        }
                    }
                }
                
                // FIX Feature 5 & 7: The structured sequence above (from ChatResponse.Choices) is the
                // authoritative record of the turn — it already contains any tool-call/result messages
                // AND the final assistant text. Only fall back to the raw streamed text when the
                // structured capture produced no assistant text block (empty Choices, or a provider that
                // streamed text without surfacing a final assistant message). This prevents the response
                // from being duplicated on reload (once from Choices, once from the stream) while
                // guaranteeing the assistant answer is never lost.
                if (!string.IsNullOrEmpty(accumulatedContent))
                {
                    bool hasAssistantText = storedContents.Any(b => b.Role == "assistant" && !string.IsNullOrEmpty(b.Text));
                    if (!hasAssistantText)
                    {
                        storedContents.Add(new ContentBlock { Role = "assistant", Text = accumulatedContent });
                    }
                }
                
                if (pause != null) storedContents.Add(new ContentBlock { Role = "assistant", Pause = pause });
                if (storedContents.Count > 0)
                {
                    serializedBlocks = System.Text.Json.JsonSerializer.Serialize(storedContents);
                    if (acceptedCompletionCallId != null)
                        assistantMessage = assistantMessage with { ContentBlocks = storedContents };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to serialize content blocks");
            }

            var assistantTurn = new ConversationTurn
            {
                Role = "assistant",
                Content = accumulatedContent,
                SerializedContentBlocks = serializedBlocks,
                Timestamp = DateTime.UtcNow
            };
            await _sessionManager.AddMessageAsync(sessionId, assistantTurn).ConfigureAwait(false);
            assistantTurnStored = true;
            FireStreamingCompleted(wasCancelled);

            _logger.LogInformation("Chat response completed for session {SessionId}: {CharCount} chars",
                sessionId, accumulatedContent.Length);
        }
        catch (OperationCanceledException)
        {
            wasCancelled = callerToken.IsCancellationRequested;
            if (!wasCancelled)
            {
                Escalate("The agent took too long to complete the task and the turn timeout was reached. Some tool operations may have been aborted.", AgentPauseReason.Timeout);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat error for session {SessionId}", sessionId);
            Escalate("The agent could not continue: " + ex.Message);
        }
        finally
        {
            FireStreamingCompleted(wasCancelled);
            if (!assistantTurnStored && (assistantMessage != null || allMessagesIncludingTools.Count > 0))
            {
                try
                {
                    // Cancellation during a retry delay must not discard already completed actions.
                    var blocks = new List<ContentBlock>();
                    foreach (var message in allMessagesIncludingTools)
                        foreach (var content in message.Contents)
                        {
                            if (content is TextContent text)
                                blocks.Add(new ContentBlock { Role = message.Role.Value, Text = text.Text });
                            else if (content is FunctionCallContent call)
                                blocks.Add(new ContentBlock { Role = message.Role.Value, CallId = call.CallId, Name = call.Name, Arguments = call.Arguments });
                            else if (content is FunctionResultContent result)
                                blocks.Add(new ContentBlock { Role = message.Role.Value, CallId = result.CallId, Result = result.Result?.ToString() });
                        }
                    if (pause != null) blocks.Add(new ContentBlock { Role = "assistant", Pause = pause });
                    await _sessionManager.AddMessageAsync(sessionId, new ConversationTurn
                    {
                        Role = "assistant", Content = accumulatedContent, Timestamp = DateTime.UtcNow,
                        SerializedContentBlocks = blocks.Count > 0 ? JsonSerializer.Serialize(blocks) : null
                    }).ConfigureAwait(false);
                }
                catch (Exception storageError) { _logger.LogError(storageError, "Could not persist interrupted turn {SessionId}", sessionId); }
            }
            // Restore the previous suppression flag state (in case of nested calls)
            if (hadSuppressedTx)
                AiAssistant.Llm.Services.DiagnosticsLoggingChatClient.ActiveConversationTransaction.Value = prevSuppressedTx;
            else
                AiAssistant.Llm.Services.DiagnosticsLoggingChatClient.ActiveConversationTransaction.Value = null;

            // Finalize the conversation-level transaction with aggregated data
            conversationStopwatch.Stop();
            if (conversationTransaction != null)
            {
                conversationTransaction.Duration = conversationStopwatch.Elapsed;
                conversationTransaction.Status = wasCancelled ? "Cancelled" : escalated ? "Needs user input" : "200 OK";
                if (!string.IsNullOrEmpty(accumulatedContent))
                    conversationTransaction.ResponsePayload = JsonSerializer.Serialize(new { Content = accumulatedContent, Messages = allMessagesIncludingTools }, new JsonSerializerOptions { WriteIndented = true });
            }
            if (pause != null) AgentPaused?.Invoke(this, pause);
        }
    }

    public async Task<IReadOnlyList<CheckpointInfo>> GetCheckpointsAsync(string sessionId)
    {
        return await _checkpointService.GetCheckpointsForSessionAsync(sessionId);
    }

    public async Task<(bool Success, string ErrorMessage)> RestoreCheckpointAsync(string checkpointId, string restoreType, CancellationToken ct = default)
    {
        var sessionId = ActiveSession?.Id;
        if (sessionId == null) return (false, "Open a conversation before restoring a checkpoint.");
        using var taskLock = await _taskLockService.AcquireLockAsync(sessionId, ct).ConfigureAwait(false);
        var checkpoints = await _checkpointService.GetCheckpointsForSessionAsync(sessionId);
        if (ActiveSession?.Id != sessionId)
            return (false, "The active conversation changed. Choose the checkpoint again.");
        if (!checkpoints.Any(c => c.Id == checkpointId))
            return (false, "This checkpoint does not belong to the current conversation.");
        using var explorerDeferral = _vsEnvService.DeferSolutionExplorerRefresh();
        var result = await _checkpointService.RestoreCheckpointAsync(checkpointId, restoreType, ct);
        if (result.Success && restoreType != "workspace")
            _contextUsage.TryRemove(sessionId, out _);
        if (restoreType == "workspace" || restoreType == "taskAndWorkspace")
            await RefreshExplorerAsync().ConfigureAwait(false);
        return result;
    }

    private async Task RefreshExplorerAsync()
    {
        try { await _vsEnvService.RefreshSolutionExplorerAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not refresh Solution Explorer after file changes"); }
    }

    public async Task<bool> HasUnresolvedToolCallsAsync(string sessionId)
    {
        try
        {
            var history = (await _sessionManager.GetConversationHistoryAsync(sessionId)).ToList();
            var lastTurn = history.LastOrDefault(t => t.Role == "assistant");
            if (lastTurn == null || string.IsNullOrWhiteSpace(lastTurn.SerializedContentBlocks))
                return false;

            var blocks = System.Text.Json.JsonSerializer.Deserialize<List<AiAssistant.Core.Models.ContentBlock>>(lastTurn.SerializedContentBlocks);
            if (blocks == null || blocks.Count == 0) return false;

            var callIds   = blocks.Where(b => b.Name   != null).Select(b => b.CallId).ToHashSet();
            var resultIds = blocks.Where(b => b.Result != null).Select(b => b.CallId).ToHashSet();

            // If any call has no matching result → unresolved
            return callIds.Any(id => !resultIds.Contains(id));
        }
        catch
        {
            return false; // Never throw — this is a best-effort check
        }
    }

    private async Task<string> BuildEphemeralFileContextAsync(List<ConversationTurn> history, int totalBudgetChars)
    {
        var recentHistory = history.AsEnumerable().Reverse().Take(40).ToList();
        
        var explicitFilesMap = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var dependencyFilesMap = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var turn in recentHistory)
        {
            if (turn.Metadata != null)
            {
                if (turn.Metadata.TryGetValue("MentionedFiles", out var mentionedObj))
                {
                    var mentionedJson = NormalizeJsonElement(mentionedObj) as string;
                    if (!string.IsNullOrEmpty(mentionedJson))
                    {
                        try {
                            var files = System.Text.Json.JsonSerializer.Deserialize<List<string>>(mentionedJson);
                            if (files != null)
                            {
                                foreach (var file in files) { explicitFilesMap[file] = true; }
                            }
                        } catch { }
                    }
                }
                
                if (turn.Metadata.TryGetValue("DependencyFiles", out var depObj))
                {
                    var depJson = NormalizeJsonElement(depObj) as string;
                    if (!string.IsNullOrEmpty(depJson))
                    {
                        try {
                            var files = System.Text.Json.JsonSerializer.Deserialize<List<string>>(depJson);
                            if (files != null)
                            {
                                foreach (var file in files) { dependencyFilesMap[file] = true; }
                            }
                        } catch { }
                    }
                }
            }
        }
        
        var explicitFiles = explicitFilesMap.Keys.ToList();
        var dependencyFiles = dependencyFilesMap.Keys.Where(d => !explicitFilesMap.ContainsKey(d)).ToList();
        
        if (explicitFiles.Count == 0 && dependencyFiles.Count == 0)
            return string.Empty;

        var fileContextBuilder = new System.Text.StringBuilder();
        fileContextBuilder.AppendLine("<file_context generated=\"true\">");
        
        int explicitBudget = totalBudgetChars;
        int floorBudget = 400;

        int perExplicitBudget = explicitFiles.Count > 0 ? explicitBudget / explicitFiles.Count : 0;
        int usedExplicit = 0;
        
        if (explicitFiles.Count > 0)
        {
            fileContextBuilder.AppendLine("<explicit>");
            int idx = 1;
            foreach (var filePath in explicitFiles)
            {
                if (perExplicitBudget < floorBudget)
                {
                     fileContextBuilder.AppendLine($"[{idx}] {System.IO.Path.GetFileName(filePath)} — OMITTED — below per-file budget floor");
                     idx++;
                     continue;
                }
                var (output, charsUsed) = await ProcessFileForContextAsync(filePath, perExplicitBudget, idx).ConfigureAwait(false);
                fileContextBuilder.AppendLine(output);
                usedExplicit += charsUsed;
                idx++;
            }
            fileContextBuilder.AppendLine("</explicit>");
        }

        if (dependencyFiles.Count > 0)
        {
            var workspaceRoot = await _vsEnvService.GetWorkspaceRootAsync().ConfigureAwait(false);
            fileContextBuilder.AppendLine("<dependencies auto_injected=\"true\">");
            int idx = 1;
            foreach (var filePath in dependencyFiles)
            {
                 var relPath = AiAssistant.Tools.Functions.WorkspacePathResolver.ToRelativePath(filePath, workspaceRoot);
                 fileContextBuilder.AppendLine($"[dep] {relPath} — OMITTED — NOT INJECTED");
                 idx++;
            }
            fileContextBuilder.AppendLine("</dependencies>");
        }

        fileContextBuilder.AppendLine("</file_context>");
        return fileContextBuilder.ToString();
    }
    
    private async Task<(string Content, int CharsUsed)> ProcessFileForContextAsync(string filePath, int maxBudgetChars, int index)
    {
        try
        {
            var content = await Task.Run(() => System.IO.File.Exists(filePath) ? System.IO.File.ReadAllText(filePath) : "").ConfigureAwait(true);
            var skeleton = AiAssistant.Engine.SkeletonEngine.ParserRouter.Parse(filePath, content, maxBudgetChars);
            
            var relativePath = filePath;
            var workspaceRoot = await _vsEnvService.GetWorkspaceRootAsync().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(workspaceRoot) && filePath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
            {
                relativePath = filePath.Substring(workspaceRoot.Length).TrimStart(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{index}] {relativePath.Replace('\\', '/')} — {skeleton.StatusDetail}");
            if (skeleton.StatusKind != AiAssistant.Engine.SkeletonEngine.ContextStatus.Omitted)
            {
                sb.AppendLine("```");
                sb.AppendLine(skeleton.Outline);
                sb.AppendLine("```");
            }
            
            return (sb.ToString(), skeleton.Outline.Length);
        }
        catch (Exception ex)
        {
            return ($"[{index}] {System.IO.Path.GetFileName(filePath)} — OMITTED — error: {ex.Message}", 0);
        }
    }

    public async Task<IReadOnlyList<ChatMessage>> GetSessionMessagesAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

        var history = await _sessionManager.GetConversationHistoryAsync(sessionId).ConfigureAwait(false);
        
        // Estimate token count for loaded session
        int maxContextTokens = 1000000;
        var providerId = ActiveSession?.ProviderId ?? "openai";
        var profile = await _providerRepo.GetByIdAsync(providerId).ConfigureAwait(false);
        if (profile != null) maxContextTokens = profile.ContextWindowTokens;
        var currentTokens = history.Sum(t => 
        {
            if (!string.IsNullOrEmpty(t.SerializedContentBlocks))
            {
                try
                {
                    var blocks = System.Text.Json.JsonSerializer.Deserialize<List<ContentBlock>>(t.SerializedContentBlocks);
                    if (blocks != null)
                    {
                        int tokens = 0;
                        foreach(var block in blocks)
                        {
                            if (block.Text != null) tokens += ContextCompactor.EstimateTokens(block.Text);
                            if (block.Arguments != null) tokens += ContextCompactor.EstimateTokens(System.Text.Json.JsonSerializer.Serialize(block.Arguments));
                            if (block.Result != null) tokens += ContextCompactor.EstimateTokens(block.Result);
                            tokens += 10;
                        }
                        return tokens;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize content blocks for token estimation in turn {TurnId}", t.Id);
                }
            }
            return ContextCompactor.EstimateTokens(t.Content);
        });
        // FIX Feature 5: Only update the token gauge when loading the ACTIVE session's messages.
        // HistoryPanel.LoadHistoryAsync calls this for every session to fetch its first prompt;
        // firing TokenUsageChanged for each made the gauge rubber-band to an arbitrary session.
        if (ActiveSession != null && ActiveSession.Id == sessionId)
        {
            TokenUsageChanged?.Invoke(this, _contextUsage.TryGetValue(sessionId, out var latestUsage)
                && latestUsage.Max == maxContextTokens ? latestUsage : (currentTokens, maxContextTokens));
        }

        return history.Select(m =>
        {
            var displayContent = m.Content;
            if (!string.IsNullOrEmpty(m.SerializedContentBlocks))
            {
                try
                {
                    var blocks = System.Text.Json.JsonSerializer.Deserialize<List<ContentBlock>>(m.SerializedContentBlocks);
                    if (blocks != null && blocks.Any(b => b.Name != null || b.Result != null))
                    {
                        // The UI receives the structured blocks separately and renders an
                        // activity timeline. Keep only the assistant's prose in Content;
                        // otherwise historic chats would show each tool both in the timeline
                        // and again as markdown text.
                        var text = new System.Text.StringBuilder();
                        foreach (var block in blocks)
                        {
                            if (!string.IsNullOrEmpty(block.Text))
                            {
                                text.Append(block.Text);
                            }
                        }
                        displayContent = text.ToString();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to render content blocks for display in turn {TurnId}", m.Id);
                    // FIX: Provide better fallback - try to extract what we can from the JSON
                    // instead of just showing an error message
                    try
                    {
                        // Attempt to show the basic text content at least
                        var doc = System.Text.Json.JsonDocument.Parse(m.SerializedContentBlocks);
                        var root = doc.RootElement;
                        var fallbackText = new System.Text.StringBuilder();
                        
                        if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var element in root.EnumerateArray())
                            {
                                if (element.TryGetProperty("Text", out var textProp) && textProp.ValueKind == System.Text.Json.JsonValueKind.String)
                                {
                                    fallbackText.Append(textProp.GetString());
                                }
                                if (element.TryGetProperty("Name", out var nameProp) && nameProp.ValueKind == System.Text.Json.JsonValueKind.String)
                                {
                                    fallbackText.AppendLine($"\nðŸ”§ Tool: {nameProp.GetString()}");
                                }
                            }
                        }
                        
                        if (fallbackText.Length > 0)
                        {
                            displayContent = fallbackText.ToString() + "\n\n⚠️ *(Some tool details could not be fully rendered)*";
                        }
                        else
                        {
                            displayContent = "⚠️ *Could not render this message (data format error). The conversation history is intact and will continue normally.*";
                        }
                    }
                    catch
                    {
                        // Last resort fallback
                        displayContent = "⚠️ *Could not render this message (data format error). The conversation history is intact and will continue normally.*";
                    }
                }
            }
            IReadOnlyList<ContentBlock>? contentBlocks = null;
            if (!string.IsNullOrEmpty(m.SerializedContentBlocks))
            {
                try
                {
                    contentBlocks = System.Text.Json.JsonSerializer.Deserialize<List<ContentBlock>>(m.SerializedContentBlocks);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to restore content blocks for UI display in turn {TurnId}", m.Id);
                }
            }

            return new ChatMessage(m.Id, m.Role, displayContent, m.Timestamp, ContentBlocks: contentBlocks, OriginalPrompt: m.OriginalPrompt);
        }).ToList();
    }

    public async Task<IReadOnlyList<SessionSummary>> GetSessionSummariesAsync()
    {
        return (await _sessionManager.GetSessionSummariesAsync().ConfigureAwait(false)).ToList();
    }

    public async Task<IReadOnlyList<Session>> GetSessionsAsync()
    {
        return (await _sessionManager.GetAllSessionsAsync().ConfigureAwait(false)).ToList();
    }

    public async Task<Session> CreateSessionAsync(string name, string providerId, string modelId, string systemPromptId)
    {
        var session = await _sessionManager.CreateSessionAsync(name, providerId, modelId, systemPromptId).ConfigureAwait(false);
        if (session.ActiveMode != _activeMode)
        {
            session.ActiveMode = _activeMode;
            await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
        }
        ActiveSession = session;
        return session;
    }

    public async Task DeleteSessionAsync(string sessionId)
    {
        await _sessionManager.DeleteSessionAsync(sessionId).ConfigureAwait(false);
        if (ActiveSession?.Id == sessionId)
            ActiveSession = null;
    }

    public async Task RenameSessionAsync(string sessionId, string newName)
    {
        var session = await _sessionManager.GetSessionAsync(sessionId).ConfigureAwait(false);
        if (session != null)
        {
            session.Name = newName;
            await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
            if (ActiveSession?.Id == sessionId)
                ActiveSession = session;
        }
    }

    public async Task SetActiveSessionAsync(string sessionId)
    {
        await _sessionManager.SetActiveSessionAsync(sessionId).ConfigureAwait(false);
        ActiveSession = await _sessionManager.GetActiveSessionAsync().ConfigureAwait(false);
        // Persist the active session to settings file
        await ApexCodePackage.SaveActiveSessionAsync(sessionId).ConfigureAwait(false);
    }

    public async Task UpdateSessionModelAsync(string sessionId, string providerId, string modelId)
    {
        var session = await _sessionManager.GetSessionAsync(sessionId).ConfigureAwait(false);
        if (session != null)
        {
            session.ProviderId = providerId;
            session.ModelId = modelId;
            await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
            if (ActiveSession?.Id == sessionId)
                ActiveSession = session;
        }
    }

    /// <summary>
    /// Refreshes the model cache by calling the OpenAI API to list available models.
    /// Called when a provider is first configured.
    /// </summary>
    public async Task RefreshModelCacheAsync(string providerId, string apiKey)
    {
        try
        {
            var modelCacheRepo = ApexCodePackage.SystemServiceProvider?.GetService(typeof(AiAssistant.Storage.Repositories.IModelCacheRepository)) as AiAssistant.Storage.Repositories.IModelCacheRepository;
            if (modelCacheRepo == null)
            {
                _logger.LogWarning("ModelCacheRepository not available, skipping model cache refresh");
                return;
            }

            _logger.LogInformation("Refreshing model cache for provider {ProviderId}", providerId);
            await modelCacheRepo.RefreshFromApiAsync(providerId, apiKey).ConfigureAwait(false);
            _logger.LogInformation("Model cache refreshed for provider {ProviderId}", providerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh model cache for provider {ProviderId}", providerId);
        }
    }


    private static object? NormalizeJsonElement(object? value)
    {
        if (value is System.Text.Json.JsonElement je)
        {
            return je.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => je.GetString(),
                System.Text.Json.JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDouble(),
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.Null => null,
                _ => je.GetRawText() // Arrays/objects stay as raw JSON strings
            };
        }
        return value;
    }
    private static int EstimateMessageTokens(Microsoft.Extensions.AI.ChatMessage msg)
    {
        int tokens = 0;
        foreach (var content in msg.Contents)
        {
            if (content is TextContent tc)
                tokens += ContextCompactor.EstimateTokens(tc.Text ?? "");
            else if (content is FunctionCallContent fcc)
            {
                tokens += ContextCompactor.EstimateTokens(fcc.Name ?? "");
                if (fcc.Arguments != null)
                    tokens += ContextCompactor.EstimateTokens(
                        System.Text.Json.JsonSerializer.Serialize(fcc.Arguments));
                tokens += 10;
            }
            else if (content is FunctionResultContent frc)
            {
                tokens += ContextCompactor.EstimateTokens(frc.Result?.ToString() ?? "");
                tokens += 10;
            }
        }
        return Math.Max(tokens, ContextCompactor.EstimateTokens(msg.Text ?? ""));
    }

    private static string FormatToolParams(ToolCallInfo info)
    {
        if (info.Arguments == null || info.Arguments.Count == 0) return "";
        var parts = info.Arguments.Select(kv =>
        {
            var valStr = kv.Value?.ToString() ?? "null";
            if (valStr.Length > 40) valStr = valStr.Substring(0, 37) + "...";
            return $"{kv.Key}=\"{valStr}\"";
        });
        return string.Join(", ", parts);
    }
    private void ProcessStreamingUpdate(
        Microsoft.Extensions.AI.ChatResponseUpdate update,
        System.Text.StringBuilder sb,
        List<Microsoft.Extensions.AI.ChatResponseUpdate> updates,
        ref string accumulatedContent,
        ref ChatMessage assistantMessage,
        HashSet<string> seenCallIds)
    {
        updates.Add(update);
        if (!string.IsNullOrEmpty(update.Text))
        {
            sb.Append(update.Text);
            accumulatedContent = sb.ToString();
            assistantMessage = assistantMessage with { Content = accumulatedContent };
            StreamingUpdated?.Invoke(this, assistantMessage);
        }

    }

    private static bool IsTerminalTool(string name) => 
        name == "plan_mode_respond" || name == "propose_plan" || name == "attempt_completion";

    private static async Task<bool> DisposeStreamAsync(IAsyncEnumerator<ChatResponseUpdate> enumerator)
    {
        await enumerator.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    private static bool IsFailedToolResult(string result)
    {
        var text = result.TrimStart();
        if (text.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("[Error]", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Blocked", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.ValueKind == JsonValueKind.Object)
                return (json.RootElement.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) ||
                    (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null);
        }
        catch (JsonException) { }
        return false;
    }

    private static bool IsCommandProgressResult(string toolName, string result)
    {
        if (toolName != "execute_command" && toolName != "read_command_output" && toolName != "stop_command") return false;
        try
        {
            using var json = JsonDocument.Parse(result);
            var root = json.RootElement;
            if (!root.TryGetProperty("session_id", out _)) return false;
            // Drain all diagnostics before charging a failure against the recovery budget.
            // Stopping an owned process is a successful stop operation, not a successful build.
            return root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True ||
                toolName == "stop_command" && root.TryGetProperty("status", out var status) && status.GetString() == "cancelled";
        }
        catch (JsonException) { return false; }
    }
    private static bool IsRateLimitError(Exception ex)
    {
        return LlmErrorClassifier.IsRateLimitError(ex);
    }

    private static bool IsNetworkError(Exception ex)
    {
        return ex is HttpRequestException ||
               ex is System.IO.IOException ||
               ex is System.Net.Sockets.SocketException ||
               ex is TaskCanceledException tce && !tce.CancellationToken.IsCancellationRequested ||
               ex.InnerException is HttpRequestException ||
               ex.InnerException is System.Net.Sockets.SocketException ||
               ex.InnerException is System.IO.IOException;
    }

    private static bool IsAuthError(Exception ex)
    {
        return LlmErrorClassifier.IsAuthError(ex) ||
               (!IsRateLimitError(ex) && !IsNetworkError(ex) &&
                (ex.Message.Contains("401") || ex.Message.Contains("Unauthorized") ||
                 ex.Message.Contains("invalid_api_key") || ex.Message.Contains("Incorrect API key")));
    }

    // --- Enhanced Plan System Orchestration ---
    public void RaisePlanProposedEvent(Plan plan) => EnhancedPlanProposed?.Invoke(this, plan);
    public void RaiseQuestionAskedEvent(Plan plan) => EnhancedQuestionAsked?.Invoke(this, plan);
    public void RaiseWalkthroughGeneratedEvent(Plan plan) => EnhancedWalkthroughGenerated?.Invoke(this, plan);
    public void RaiseScrollToPlanEvent(string planId) => ScrollToActivePlanRequested?.Invoke(this, planId);

    public async Task TriggerSystemPromptInjectionAsync(CancellationToken cancellationToken)
    {
        var sessionId = ActiveSession?.Id;
        if (sessionId == null) return;

        // Inject the plan-approved system turn so the LLM knows to call generate_tasks.
        var turn = new ConversationTurn
        {
            Id = Guid.NewGuid().ToString(),
            Role = "user",
            Content = "[SYSTEM INSTRUCTION: The user has approved the plan. Call generate_tasks immediately.]",
            Timestamp = DateTime.UtcNow
        };
        await _sessionManager.AddMessageAsync(sessionId, turn);
        if (ActiveSession?.Id != sessionId) return;
        await SendMessageAsync(sessionId, "[Auto-continuation: Plan Approved]", null, cancellationToken);

        // After the LLM generates tasks the plan status becomes TasksGenerated.
        // Immediately kick off parallel execution without waiting for the user.
        var activePlan = await _planService.GetActivePlanAsync(sessionId, cancellationToken);
        if (ActiveSession?.Id == sessionId && activePlan != null &&
            activePlan.Status == PlanStatus.TasksGenerated && activePlan.Tasks.Count > 0)
        {
            _ = TriggerParallelExecutionAsync(sessionId, activePlan, cancellationToken);
        }
    }

    /// <summary>
    /// Parallel execution scheduler.
    /// Analyses the task dependency graph, fans out all tasks whose dependencies are already
    /// satisfied concurrently, retries failures (up to MaxRetries), and fires
    /// <see cref="ExecutionSummaryReady"/> when the full batch is processed.
    /// </summary>


    public async Task TriggerParallelExecutionAsync(string sessionId, Plan plan, CancellationToken cancellationToken)
    {
        var maxTaskRetries = ResilienceOptions.Snapshot().MaxPlanTaskRetries;
        if (plan.Tasks == null || plan.Tasks.Count == 0) return;

        // Track retry counts per task id.
        var retryCounts = plan.Tasks.ToDictionary(t => t.Id, _ => 0);

        // Keep processing waves until all tasks are in a terminal state.
        bool anyProgress = true;
        while (anyProgress && !cancellationToken.IsCancellationRequested)
        {
            anyProgress = false;

            // Collect tasks that are ready to run (dependencies met, not yet started/done).
            var readyTasks = plan.Tasks
                .Where(t => t.Status == AiAssistant.Core.Models.TaskStatus.Pending ||
                            (t.Status == AiAssistant.Core.Models.TaskStatus.Failed &&
                             retryCounts[t.Id] <= maxTaskRetries))
                .Where(t =>
                {
                    if (t.Dependencies == null || t.Dependencies.Count == 0) return true;
                    return t.Dependencies.All(depId =>
                        plan.Tasks.Any(d => d.Id == depId &&
                                            d.Status == AiAssistant.Core.Models.TaskStatus.Completed));
                })
                .ToList();

            if (readyTasks.Count == 0) break;

            anyProgress = true;

            // Fan out all ready tasks concurrently.
            var taskRunners = readyTasks.Select(async planTask =>
            {
                if (retryCounts[planTask.Id] > 0)
                {
                    _outputLogger.Log(LogCategory.Agent,
                        $"[ParallelExec] Retrying task '{planTask.Title}' (attempt {retryCounts[planTask.Id] + 1}/{maxTaskRetries + 1})");
                }
                else
                {
                    _outputLogger.Log(LogCategory.Agent, $"[ParallelExec] Starting task '{planTask.Title}'");
                }

                retryCounts[planTask.Id]++;

                try
                {
                    await _planService.StartTaskAsync(sessionId, plan.Id, planTask.Id, cancellationToken);

                    // Check settings-based per-tool approval before executing the task turn.
                    // If ANY tool required by this task has approval mode "prompt", we inject
                    // a waiting-for-approval status and do not auto-proceed.
                    bool needsApproval = planTask.ToolsUsed?.Any(tool =>
                        _settingsService.GetToolApprovalMode(tool) == "prompt") ?? false;

                    if (needsApproval)
                    {
                        // Surface approval request to the UI by marking the task as blocked.
                        planTask.Status = AiAssistant.Core.Models.TaskStatus.Blocked;
                        planTask.FailureMessage = "Waiting for user approval (tool permission required)";
                        _outputLogger.Log(LogCategory.Agent,
                            $"[ParallelExec] Task '{planTask.Title}' paused — waiting for tool permission");
                        // The user will use Retry after granting permission.
                        return;
                    }

                    // Send an LLM turn to perform the task.
                    var prompt = $"[SYSTEM INSTRUCTION: Execute plan task '{planTask.Title}'. " +
                                 $"Task description: {planTask.Description}. " +
                                 "Use start_task/complete_task to mark progress. " +
                                 "Do not ask the user for confirmation; proceed directly.]";
                    await SendMessageAsync(sessionId, prompt, null, cancellationToken);

                    // Re-read the task status from the persisted plan to confirm completion.
                    var updatedPlan = await _planService.GetActivePlanAsync(sessionId, cancellationToken);
                    var updatedTask = updatedPlan?.Tasks?.FirstOrDefault(t => t.Id == planTask.Id);

                    if (updatedTask?.Status == AiAssistant.Core.Models.TaskStatus.Completed)
                    {
                        // Sync the in-memory plan object for the next wave.
                        planTask.Status = AiAssistant.Core.Models.TaskStatus.Completed;
                        _outputLogger.Log(LogCategory.Agent, $"[ParallelExec] Task '{planTask.Title}' completed ✓");
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"Task '{planTask.Title}' did not complete successfully.");
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    var msg = ex.Message.Length > 200 ? ex.Message.Substring(0, 200) + "..." : ex.Message;
                    _outputLogger.Log(LogCategory.Agent,
                        $"[ParallelExec] Task '{planTask.Title}' failed: {msg}");

                    if (retryCounts[planTask.Id] <= maxTaskRetries)
                    {
                        // Keep as Failed so the next wave picks it up for retry.
                        planTask.Status = AiAssistant.Core.Models.TaskStatus.Failed;
                        planTask.FailureMessage = msg;
                        await _planService.FailTaskAsync(sessionId, plan.Id, planTask.Id, msg, cancellationToken);
                    }
                    else
                    {
                        // Retries exhausted — mark terminal failure.
                        planTask.Status = AiAssistant.Core.Models.TaskStatus.Failed;
                        planTask.FailureMessage = $"Failed after {maxTaskRetries + 1} attempts: {msg}";
                        await _planService.FailTaskAsync(sessionId, plan.Id, planTask.Id,
                            planTask.FailureMessage, cancellationToken);
                        _outputLogger.Log(LogCategory.Agent,
                            $"[ParallelExec] Task '{planTask.Title}' exhausted retries — marked Failed");
                    }
                }
            });

            await Task.WhenAll(taskRunners);
        }

        // All waves done — fire the summary event so the UI can show the failure report.
        var finalPlan = await _planService.GetActivePlanAsync(sessionId, cancellationToken) ?? plan;
        ExecutionSummaryReady?.Invoke(this, finalPlan);
        _outputLogger.Log(LogCategory.Agent,
            $"[ParallelExec] Execution complete. " +
            $"Completed: {finalPlan.Tasks?.Count(t => t.Status == AiAssistant.Core.Models.TaskStatus.Completed)} / {finalPlan.Tasks?.Count}. " +
            $"Failed: {finalPlan.Tasks?.Count(t => t.Status == AiAssistant.Core.Models.TaskStatus.Failed)}");
    }

    public async Task TriggerAutoExecutionStepAsync(CancellationToken cancellationToken)
    {
        if (ActiveSession == null) return;

        var turn = new ConversationTurn
        {
            Id = Guid.NewGuid().ToString(),
            Role = "user",
            Content = "[SYSTEM INSTRUCTION: Proceed to the next step. If blocked, ask a question. If done, generate a walkthrough.]",
            Timestamp = DateTime.UtcNow
        };
        await _sessionManager.AddMessageAsync(ActiveSession.Id, turn);
        await SendMessageAsync(ActiveSession.Id, "[Auto-continuation: Next Step]", null, cancellationToken);
    }

    public async Task<int> GetFileContextBudgetCharsAsync()
    {
        var providerId = ActiveSession?.ProviderId ?? "openai";
        var profile = await _providerRepo.GetByIdAsync(providerId).ConfigureAwait(false);
        
        // Edge case: unknown model -> default 8k (conservative)
        int maxContextTokens = profile?.ContextWindowTokens > 0 ? profile.ContextWindowTokens : 8192;

        int systemPromptTokens = 1500; // rough estimate
        int outputReserve = 4096;
        int protectedHistory = 10000; // baseline protected history limit

        // Math: min(appetite_chars [24,000], capacity_chars [subtractive, ratio 3], EFFICACY_CAP_CHARS [200,000])
        int capacity_tokens = maxContextTokens - systemPromptTokens - outputReserve - protectedHistory;
        
        // Edge case: negative capacity -> clamp to 0 (fails safe into all-OMITTED)
        if (capacity_tokens < 0) capacity_tokens = 0;
        
        int capacity_chars = capacity_tokens * 3;
        int appetite_chars = 24000;
        int efficacyCapChars = 200000;

        return Math.Min(appetite_chars, Math.Min(capacity_chars, efficacyCapChars));
    }
}


