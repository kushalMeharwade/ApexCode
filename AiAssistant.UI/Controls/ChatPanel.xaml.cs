using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AiAssistant.Core.Models;
using AiAssistant.Core.Services;
using AiAssistant.Engine.Services;
using AiAssistant.Engine.SkeletonEngine;
using AiAssistant.Storage.Models;
using AiAssistant.Storage.Repositories;
using AiAssistant.Storage.Services;
using AiAssistant.UI.ViewModels;

namespace AiAssistant.UI.Controls;

public partial class ChatPanel : UserControl, INotifyPropertyChanged
{
    public static readonly RoutedUICommand FocusPersonaCmd = new RoutedUICommand("Focus Persona", "FocusPersonaCmd", typeof(ChatPanel));
    public static readonly RoutedUICommand FocusDatabaseCmd = new RoutedUICommand("Focus Database", "FocusDatabaseCmd", typeof(ChatPanel));
    public static readonly RoutedUICommand CopyMessageCmd = new RoutedUICommand("Copy Message", "CopyMessageCmd", typeof(ChatPanel));
    public static readonly RoutedUICommand OpenFileCmd = new RoutedUICommand("Open File", "OpenFileCmd", typeof(ChatPanel));
    public static readonly RoutedUICommand ViewDiffCmd = new RoutedUICommand("View Diff", "ViewDiffCmd", typeof(ChatPanel));
    private IChatService? _chatService;
    private IPersonaService? _personaService;
    private ITelemetryService? _telemetryService;
    private IProviderProfileRepository? _providerRepo;
    private IFileBackupService? _backupService;
    private IndexingService? _indexingService;
    private ISystemPromptRepository? _promptRepo;
    private IDatabaseConnectionService? _dbService;
    private AiAssistant.Llm.Services.IChatClientFactory? _chatClientFactory;

    private IOutputLogger? _outputLogger;
    private IVisualStudioEnvironmentService? _vsEnvService;
    private IApprovalService? _approvalService;
    private FileSystemWatcher? _fileWatcher;
    private ICheckpointService? _checkpointService;
    private IPlanService? _planService;

    private Microsoft.VisualStudio.Threading.JoinableTask? _loadWorkspaceFilesTask;
    private Microsoft.VisualStudio.Threading.JoinableTask? _selectMentionTask;

    private ObservableCollection<ChatMessageViewModel> _messages = new();
    private string _errorMessage = "";
    private bool _isStreaming;
    private bool _isDarkTheme = true;
    private ICSharpCode.AvalonEdit.Document.TextSegmentCollection<MentionSpan>? _mentionSpans;
    private System.Collections.Immutable.ImmutableList<string> _workspaceFiles = System.Collections.Immutable.ImmutableList<string>.Empty;
    private int _mentionTriggerIndex = -1;
    private readonly object _streamUpdateLock = new();
    private string? _pendingStreamContent;
    private bool _streamUpdateScheduled;
    private int _scrollScheduled;
    private ScrollViewer? _messagesScrollViewer;
    // Tracks the total character count already pushed to AppendTextDelta so each
    // streaming tick only sends the NEW suffix, not the whole accumulated string.
    private int _lastStreamedLength;
    public static bool IsApprovalOverlayActive { get; private set; } = false;
    private string? _activeWorkspacePath;

    /// <summary>Reentrancy guard so a double-click cannot submit two approval turns.</summary>
    private bool _planActionInProgress;

    // FIX: Live countdown timer for retry banner
    private DispatcherTimer? _retryCountdownTimer;
    private DateTime _retryTargetTime;
    private string _retryReason = "";

    public ObservableCollection<ChatMessageViewModel> Messages
    {
        get => _messages;
        private set { _messages = value; OnPropertyChanged(); }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set { _errorMessage = value; OnPropertyChanged(); }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        private set 
        { 
            _isStreaming = value; 
            OnPropertyChanged(); 
            Dispatcher.InvokeAsync(() => 
            {
                if (MessageTextBox != null) MessageTextBox.IsEnabled = !value;
                if (SendButton != null)
                {
                    SendButton.IsEnabled = !value;
                    // FIX (Streaming Stop button): swap Send for Stop while generating.
                    SendButton.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
                }
                if (StopButton != null) StopButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            });
        }
    }

    private CancellationTokenSource? _generationCts;

    private string _connectionStatus = "⚪ Not configured";
    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set { _connectionStatus = value; OnPropertyChanged(); }
    }

    private IDiffPreviewService? _diffService;
    private ISettingsService? _settingsService;
    private IProviderChangeNotifier? _providerChangeNotifier;
    private IModelFetchService? _modelFetchService;
    private IModelCacheRepository? _modelCacheRepo;
    private AiAssistant.Core.Services.IUserPreferencesService? _userPreferences;
    private List<ProviderProfile>? _cachedProfiles;
    private readonly ILogBus? _logBus;
    private readonly AiAssistant.Core.Services.IFileDependencyResolver? _dependencyResolver;
    private bool _isAwaitingAnswer = false;
    private bool _userPressedStop = false;
    private AiAssistant.UI.ViewModels.TaskExecutionPanelViewModel? _taskExecutionViewModel;
    private string? _lastFetchedProviderId;

    public ChatPanel() : this(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null) { }

    private readonly AiAssistant.Engine.Services.ModelDownloaderService? _modelDownloader;

    public ChatPanel(IChatService? chatService, IPersonaService? personaService, IDiffPreviewService? diffService, ITelemetryService? telemetryService, ISettingsService? settingsService, IProviderProfileRepository? providerRepo, IFileBackupService? backupService, IProviderChangeNotifier? providerChangeNotifier, IModelFetchService? modelFetchService, IModelCacheRepository? modelCacheRepo, IndexingService? indexingService = null, IApprovalService? approvalService = null, ILogBus? logBus = null)
        : this(chatService, personaService, diffService, telemetryService, settingsService, providerRepo, backupService, providerChangeNotifier, modelFetchService, modelCacheRepo, indexingService, null, null, null, null, null, null, approvalService, logBus, null, null)
    {
    }

    public ChatPanel(IChatService? chatService, IPersonaService? personaService, IDiffPreviewService? diffService, ITelemetryService? telemetryService, ISettingsService? settingsService, IProviderProfileRepository? providerRepo, IFileBackupService? backupService, IProviderChangeNotifier? providerChangeNotifier, IModelFetchService? modelFetchService, IModelCacheRepository? modelCacheRepo, IndexingService? indexingService, ISystemPromptRepository? promptRepo, IDatabaseConnectionService? dbService, AiAssistant.Llm.Services.IChatClientFactory? chatClientFactory, IOutputLogger? outputLogger, IVisualStudioEnvironmentService? vsEnvService = null, AiAssistant.Engine.Services.ModelDownloaderService? modelDownloader = null, IApprovalService? approvalService = null, ILogBus? logBus = null, ICheckpointService? checkpointService = null, AiAssistant.Core.Services.IFileDependencyResolver? dependencyResolver = null, IPlanService? planService = null)
    {
        _chatService = chatService;
        _personaService = personaService;
        _planService = planService;
        _diffService = diffService;
        _telemetryService = telemetryService;
        _settingsService = settingsService;
        if (_settingsService != null)
        {
            _settingsService.ProviderSettingsChanged += OnSettingsChanged;
        }
        _userPreferences = _settingsService?.UserPreferences;
        _providerRepo = providerRepo;
        _backupService = backupService;
        _indexingService = indexingService;
        _providerChangeNotifier = providerChangeNotifier;
        _modelFetchService = modelFetchService;
        _modelCacheRepo = modelCacheRepo;
        _promptRepo = promptRepo;
        _dbService = dbService;
        _chatClientFactory = chatClientFactory;
        _outputLogger = outputLogger;
        _vsEnvService = vsEnvService;
        _modelDownloader = modelDownloader;
        _approvalService = approvalService;
        _logBus = logBus;
        _checkpointService = checkpointService;
        _dependencyResolver = dependencyResolver;
        _planService = planService;

        InitializeComponent();

        if (_chatService != null && _planService != null)
        {
            _taskExecutionViewModel = new AiAssistant.UI.ViewModels.TaskExecutionPanelViewModel(_planService, _chatService);
            if (TaskExecutionPanel != null)
            {
                TaskExecutionPanel.DataContext = _taskExecutionViewModel;
            }
        }
        else if (TaskExecutionPanel != null)
        {
            // Prevent inheriting ChatPanel's DataContext which causes WPF binding errors
            TaskExecutionPanel.DataContext = null;
        }

        Helpers.ThemeManager.ThemeChanged += OnThemeChanged;
        if (_userPreferences != null)
        {
            _userPreferences.PreferencesChanged += OnPreferencesChanged;
        }
        OnThemeChanged(null, EventArgs.Empty);

        DataContext = this;
        
        if (MessageTextBox != null && MessageTextBox.Document != null)
        {
            _mentionSpans = new ICSharpCode.AvalonEdit.Document.TextSegmentCollection<MentionSpan>(MessageTextBox.Document);
        }

        if (_backupService != null)
        {
            _backupService.CanUndoChanged += BackupService_CanUndoChanged;
            UpdateUndoButtonVisibility();
        }

        if (_indexingService != null)
        {
            _indexingService.FileIndexed += OnFileIndexed;
        }

        if (_providerChangeNotifier != null)
        {
            _providerChangeNotifier.ProvidersChanged += OnProvidersChanged;
        }

        SubscribeToChatServiceEvents();

        

        if (_diffService != null)
        {
            // Diff popup removed per user request
        }

        Loaded += ChatPanel_Loaded;
        Unloaded += ChatPanel_Unloaded;

        CommandBindings.Add(new CommandBinding(FocusPersonaCmd, (s, e) => FocusPersonaCombo()));
        CommandBindings.Add(new CommandBinding(FocusDatabaseCmd, (s, e) => FocusDatabaseCombo()));
        CommandBindings.Add(new CommandBinding(CopyMessageCmd, CopyMessageCmd_Executed));
        CommandBindings.Add(new CommandBinding(OpenFileCmd, OpenFileCmd_Executed, OpenFileCmd_CanExecute));
        CommandBindings.Add(new CommandBinding(ViewDiffCmd, ViewDiffCmd_Executed, ViewDiffCmd_CanExecute));

        // FIX (paste-to-attach files): AvalonEdit's TextArea registers its own Paste
        // CommandBinding internally, which only handles plain text. Adding a
        // PreviewExecuted handler on MessageTextBox intercepts Ctrl+V during the tunnel
        // phase, before that internal binding's Executed handler runs on TextArea, so we
        // can redirect a file-list clipboard payload to the same attach flow used by
        // drag-and-drop while leaving plain-text paste completely untouched.
        CommandManager.AddPreviewExecutedHandler(MessageTextBox, MessageTextBox_PreviewPasteExecuted);

    }

    private void OpenFileCmd_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = !string.IsNullOrWhiteSpace(e.Parameter as string);
    }

    private void OpenFileCmd_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is string path && !string.IsNullOrWhiteSpace(path) && _vsEnvService != null)
        {
            var resolvedPath = path;
            if (!System.IO.Path.IsPathRooted(resolvedPath) && !string.IsNullOrEmpty(_activeWorkspacePath))
            {
                resolvedPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(_activeWorkspacePath, resolvedPath));
            }

            if (!System.IO.File.Exists(resolvedPath))
            {
                System.Windows.MessageBox.Show($"The file no longer exists or could not be found:\n{resolvedPath}", "File Not Found", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await _vsEnvService.OpenFileAsync(resolvedPath);
                }
                catch (Exception ex)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        System.Windows.MessageBox.Show($"Failed to open file:\n{ex.Message}", "Error Opening File", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    });
                }
            });
        }
    }

    private void ViewDiffCmd_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = e.Parameter is AiAssistant.UI.ViewModels.ToolCallViewModel vm && vm.HasChangePreview;
    }

    private void ViewDiffCmd_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is AiAssistant.UI.ViewModels.ToolCallViewModel vm && _vsEnvService != null)
        {
            var title = $"Diff: {(vm.Target ?? "Tool Changes")}";
            var oldText = vm.RawRemovedText ?? string.Empty;
            var newText = vm.RawAddedText ?? string.Empty;

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AiAssistantDiffs", Guid.NewGuid().ToString("N"));
                    System.IO.Directory.CreateDirectory(tempDir);
                    
                    var oldFile = System.IO.Path.Combine(tempDir, "Original.txt");
                    var newFile = System.IO.Path.Combine(tempDir, "Modified.txt");
                    
                    System.IO.File.WriteAllText(oldFile, oldText);
                    System.IO.File.WriteAllText(newFile, newText);

                    await _vsEnvService.OpenDiffViewerAsync(oldFile, newFile, title);
                }
                catch (Exception ex)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        System.Windows.MessageBox.Show($"Failed to open diff viewer:\n{ex.Message}", "Error Opening Diff", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    });
                }
            });
        }
    }

    private void SubscribeToChatServiceEvents()
    {
        if (_chatService == null) return;

        // Loaded may run again after a tool window is hidden and shown. Remove first
        // so the subscriptions are restored without accumulating duplicate handlers.
        _chatService.MessageReceived -= OnChatMessageReceived;
        _chatService.StreamingUpdated -= OnStreamingUpdated;
        _chatService.StreamingCompleted -= OnStreamingCompleted;
        _chatService.ErrorOccurred -= OnChatError;
        _chatService.AgentPaused -= OnAgentPaused;
        _chatService.TokenUsageChanged -= OnTokenUsageChanged;
        _chatService.ToolExecuting -= OnToolExecuting;
        _chatService.ToolCompleted -= OnToolCompleted;
        if (_chatService is ICommandProgressSource oldCommands) oldCommands.CommandProgress -= OnCommandProgress;
        _chatService.CheckpointCreated -= OnCheckpointCreated;
        _chatService.RetryInitiated -= OnRetryInitiated;
        
        _chatService.EnhancedPlanProposed -= OnEnhancedPlanProposed;
        _chatService.EnhancedQuestionAsked -= OnEnhancedQuestionAsked;
        _chatService.EnhancedWalkthroughGenerated -= OnEnhancedWalkthroughGenerated;
        _chatService.EnhancedPlanUpdated -= OnEnhancedPlanUpdated;
        _chatService.ScrollToActivePlanRequested -= OnScrollToActivePlanRequested;
        _chatService.ExecutionSummaryReady -= OnExecutionSummaryReady;

        _chatService.MessageReceived += OnChatMessageReceived;
        _chatService.StreamingUpdated += OnStreamingUpdated;
        _chatService.StreamingCompleted += OnStreamingCompleted;
        _chatService.ErrorOccurred += OnChatError;
        _chatService.AgentPaused += OnAgentPaused;
        _chatService.TokenUsageChanged += OnTokenUsageChanged;
        _chatService.ToolExecuting += OnToolExecuting;
        _chatService.ToolCompleted += OnToolCompleted;
        if (_chatService is ICommandProgressSource commands) commands.CommandProgress += OnCommandProgress;
        _chatService.CheckpointCreated += OnCheckpointCreated;
        _chatService.RetryInitiated += OnRetryInitiated;
        
        _chatService.EnhancedPlanProposed += OnEnhancedPlanProposed;
        _chatService.EnhancedQuestionAsked += OnEnhancedQuestionAsked;
        _chatService.EnhancedWalkthroughGenerated += OnEnhancedWalkthroughGenerated;
        _chatService.EnhancedPlanUpdated += OnEnhancedPlanUpdated;
        _chatService.ScrollToActivePlanRequested += OnScrollToActivePlanRequested;
        _chatService.ExecutionSummaryReady += OnExecutionSummaryReady;
    }

    private void SubscribeToToolLogBus()
    {
        if (_logBus == null) return;
        _logBus.EntryPublished -= LogBus_EntryPublished;
        _logBus.EntryPublished += LogBus_EntryPublished;
    }

    /// <summary>
    /// Reveals the provider/persona/database row in the composer. The persona and
    /// database shortcuts open a dropdown, which cannot happen while the row that
    /// hosts those combo boxes is collapsed, so both entry points call this first.
    /// </summary>
    private void ShowScopeSelectors()
    {
        if (ScopeSelectorPanel == null) return;
        ScopeSelectorPanel.Visibility = Visibility.Visible;
        if (ScopeToggleBtn != null) ScopeToggleBtn.IsChecked = true;
    }

    private void ScopeToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ScopeSelectorPanel == null) return;
        var show = ScopeToggleBtn?.IsChecked == true;
        ScopeSelectorPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    public void FocusPersonaCombo()
    {
        ShowScopeSelectors();
        PersonaCombo.Focus();
        PersonaCombo.IsDropDownOpen = true;
    }

    public void FocusDatabaseCombo()
    {
        ShowScopeSelectors();
        DatabaseCombo.Focus();
        DatabaseCombo.IsDropDownOpen = true;
    }

    /// <summary>
    /// Focuses the chat input box. Invoked by FocusInputCommand's global keyboard
    /// shortcut so users can start typing a prompt without reaching for the mouse,
    /// even if the tool window wasn't open or focused yet.
    /// </summary>
    public void FocusMessageInput()
    {
        MessageTextBox.Focus();
    }

    private void BackupService_CanUndoChanged(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(UpdateUndoButtonVisibility);
    }

    private void UpdateUndoButtonVisibility()
    {
        if (UndoBtn != null)
        {
            UndoBtn.Visibility = (_backupService?.CanUndo == true) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async void UndoBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_backupService != null && _backupService.CanUndo)
        {
            try
            {
                bool success = await _backupService.UndoLastAsync();
                if (success)
                {
                    var sysMsg = new ChatMessageViewModel
                    {
                        Role = "system",
                        Content = "✓ Successfully undid the last file modification.",
                        Timestamp = DateTime.Now
                    };
                    sysMsg.AppendTextBlock(sysMsg.Content);
                    Messages.Add(sysMsg);
                    ScrollToBottom();
                }
                else
                {
                    ShowError("Failed to undo the last action. Backup might not be accessible.");
                }
            }
            catch (Exception ex)
            {
                ShowError($"Error during undo: {ex.Message}");
            }
        }
    }

    private async void CopyMessageCmd_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is string content)
        {
            try
            {
                Clipboard.SetText(content);

                if (e.OriginalSource is DependencyObject depObj)
                {
                    Button button = depObj as Button;
                    DependencyObject current = depObj;
                    while (button == null && current != null)
                    {
                        current = System.Windows.Media.VisualTreeHelper.GetParent(current);
                        button = current as Button;
                    }

                    if (button != null)
                    {
                        var originalToolTip = button.ToolTip;
                        var path = button.Content as System.Windows.Shapes.Path;
                        System.Windows.Media.Geometry originalData = null;

                        if (path != null)
                        {
                            originalData = path.Data;
                            var checkIcon = button.FindResource("IconCheck") as System.Windows.Media.Geometry;
                            if (checkIcon != null)
                            {
                                path.Data = checkIcon;
                            }
                        }

                        button.ToolTip = "Copied!";

                        await Task.Delay(2000);

                        button.ToolTip = originalToolTip;
                        if (path != null && originalData != null)
                        {
                            path.Data = originalData;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ShowError($"Failed to copy message: {ex.Message}");
            }
        }
    }


    private void OnChatMessageReceived(object? sender, ChatMessage msg)
    {
        // User messages are added by the UI immediately before calling SendMessageAsync,
        // so skip them here to avoid duplication (and to keep hidden context out of the UI).
        if (msg.Role == "user")
        {
            Dispatcher.InvokeAsync(() =>
            {
                // The optimistic bubble already contains the original visible prompt.
                // Bind it to the persisted ID before the checkpoint event arrives.
                var pending = Messages.LastOrDefault(m => m.Role == "user");
                if (pending != null) pending.Id = msg.Id;
            });
            return;
        }

        Dispatcher.InvokeAsync(() =>
        {
            var localTime = msg.Timestamp.Kind == DateTimeKind.Local ? msg.Timestamp :
                DateTime.SpecifyKind(msg.Timestamp, DateTimeKind.Utc).ToLocalTime();

            var viewModel = new ChatMessageViewModel
            {
                Id = msg.Id,
                Role = msg.Role,
                Content = msg.Content,
                Timestamp = localTime,
                IsStreaming = msg.IsStreaming
            };
            
            if (!string.IsNullOrEmpty(msg.Content))
            {
                viewModel.AppendTextBlock(msg.Content);
            }
            
            Messages.Add(viewModel);
            // Reset the delta pointer for the new streaming turn.
            _lastStreamedLength = 0;
            ScrollToBottom();
        });
    }

    private void OnEnhancedPlanProposed(object? sender, Plan plan)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            // Supersede any existing awaiting review plans
            foreach (var message in Messages)
            {
                foreach (var element in message.Elements)
                {
                    if (element is PlanProposedCardViewModel oldCard && oldCard.ReviewPanelViewModel.IsAwaitingReview)
                    {
                        oldCard.ReviewPanelViewModel.SetSuperseded();
                    }
                }
            }

            var msgViewModel = new ChatMessageViewModel
            {
                Id = Guid.NewGuid().ToString(),
                Role = "assistant",
                Content = "",
                Timestamp = DateTime.Now,
                IsStreaming = false
            };
            
            var planCard = new PlanProposedCardViewModel(plan, () => ApprovePlanForExecutionAsync(plan), (feedback) => {
                if (!string.IsNullOrWhiteSpace(feedback) && _chatService != null && _chatService.ActiveSession != null)
                {
                    var prompt = $"I have some feedback on the proposed plan:\n\n{feedback}\n\nPlease revise the plan using the revise_plan tool.";
                    _ = _chatService.SendMessageAsync(_chatService.ActiveSession.Id, prompt);
                }
            });
            
            msgViewModel.Elements.Add(planCard);
            Messages.Add(msgViewModel);
            ScrollToBottom();
        });
    }

    private async Task<bool> ApprovePlanForExecutionAsync(Plan plan)
    {
        var sessionId = _chatService?.ActiveSession?.Id;
        if (sessionId == null || _planService == null) return false;
        try
        {
            var current = await _planService.GetActivePlanAsync(sessionId);
            if (_chatService?.ActiveSession?.Id != sessionId || current == null ||
                current.Id != plan.Id || current.Status != PlanStatus.AwaitingReview) return false;

            await _planService.ApprovePlanAsync(sessionId, plan.Id);
            if (_chatService.ActiveSession?.Id != sessionId) return false;
            current.Status = PlanStatus.Approved;
            if (_taskExecutionViewModel != null) _taskExecutionViewModel.Plan = current;
            IsStreaming = true;
            StreamingProgressBar.Visibility = Visibility.Visible;
            ThinkingAnimationPanel.Visibility = Visibility.Visible;
            _ = _chatService.TriggerSystemPromptInjectionAsync(CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            ShowError($"Could not approve plan: {ex.Message}");
            return false;
        }
    }

    private void OnScrollToActivePlanRequested(object? sender, string planId)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            ChatMessageViewModel targetMsg = null;
            PlanProposedCardViewModel targetCard = null;

            foreach (var message in Messages)
            {
                foreach (var element in message.Elements)
                {
                    if (element is PlanProposedCardViewModel card && card.PlanId == planId)
                    {
                        targetCard = card;
                        targetMsg = message;
                        break;
                    }
                }
                if (targetMsg != null) break;
            }

            if (targetMsg != null && targetCard != null)
            {
                // Expand the card
                targetCard.ReviewPanelViewModel.IsCollapsed = false;

                // Wait a layout pass to ensure the card's visual tree is updated
                await System.Threading.Tasks.Task.Delay(100);

                // Scroll to the message in the ListBox
                MessagesList.ScrollIntoView(targetMsg);
            }
        });
    }

    private void OnEnhancedPlanUpdated(object? sender, Plan plan)
    {
        _ = Dispatcher.InvokeAsync(() => 
        {
            if (plan.SessionId != _chatService?.ActiveSession?.Id) return;
            if (_taskExecutionViewModel != null)
            {
                _taskExecutionViewModel.Plan = plan;
            }
            if (TaskExecutionPanel != null && _taskExecutionViewModel != null)
            {
                TaskExecutionPanel.DataContext = _taskExecutionViewModel;
            }
        });
    }

    private void OnEnhancedQuestionAsked(object? sender, Plan plan)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_isAwaitingAnswer) return;
            _isAwaitingAnswer = true;
            ComposerBorder.BorderBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#EDA200")!;
            ComposerBorder.BorderThickness = new Thickness(2);
            UpdatePlaceholderVisibility();

            var question = plan.OpenQuestions.FirstOrDefault(q => q.State == QuestionState.Pending);
            
            var activeMsg = Messages.LastOrDefault(m => m.Role == "assistant");
            if (activeMsg != null)
            {
                if (question != null)
                {
                    var task = plan.Tasks.FirstOrDefault(t => t.Id == question.TaskId);
                    var taskTitle = task?.Title ?? "Unknown Task";
                    var card = new QuestionCardViewModel(question, taskTitle, async (result) => 
                    {
                        if (_planService != null && _chatService?.ActiveSession != null)
                        {
                            await _planService.AnswerQuestionAsync(_chatService.ActiveSession.Id, plan.Id, question.Id, result);
                        }
                        
                        Dispatcher.Invoke(() => 
                        {
                            _isAwaitingAnswer = false;
                            ComposerBorder.BorderBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#33FFFFFF")!; // Default border
                            ComposerBorder.BorderThickness = new Thickness(1);
                            UpdatePlaceholderVisibility();
                        });
                    });
                    activeMsg.Elements.Add(card);
                }
                else
                {
                    activeMsg.AppendTextBlock("I have a question, but I couldn't find the details.");
                }
            }
            else
            {
                var msgViewModel = new ChatMessageViewModel
                {
                    Id = Guid.NewGuid().ToString(),
                    Role = "assistant",
                    Content = "",
                    Timestamp = DateTime.Now,
                    IsStreaming = false
                };
                if (question != null)
                {
                    var task = plan.Tasks.FirstOrDefault(t => t.Id == question.TaskId);
                    var taskTitle = task?.Title ?? "Unknown Task";
                    var card = new QuestionCardViewModel(question, taskTitle, async (result) => 
                    {
                        if (_planService != null && _chatService?.ActiveSession != null)
                        {
                            await _planService.AnswerQuestionAsync(_chatService.ActiveSession.Id, plan.Id, question.Id, result);
                        }
                        
                        Dispatcher.Invoke(() => 
                        {
                            _isAwaitingAnswer = false;
                            ComposerBorder.BorderBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#33FFFFFF")!; // Default border
                            ComposerBorder.BorderThickness = new Thickness(1);
                            UpdatePlaceholderVisibility();
                        });
                    });
                    msgViewModel.Elements.Add(card);
                }
                else
                {
                    msgViewModel.AppendTextBlock("I have a question, but I couldn't find the details.");
                }
                Messages.Add(msgViewModel);
            }
            ScrollToBottom();
        });
    }

    private void OnEnhancedWalkthroughGenerated(object? sender, Plan plan)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var msgViewModel = new ChatMessageViewModel
            {
                Id = Guid.NewGuid().ToString(),
                Role = "assistant",
                Content = "",
                Timestamp = DateTime.Now,
                IsStreaming = false
            };
            
            var walkthroughCard = new WalkthroughCardViewModel(plan);
            msgViewModel.Elements.Add(walkthroughCard);
            Messages.Add(msgViewModel);
            ScrollToBottom();
        });
    }

    /// <summary>
    /// Fires when the parallel task execution batch finishes (success or partial failure).
    /// Adds an <see cref="ExecutionSummaryCardViewModel"/> to the message list so the user
    /// can see the final status of every task without any manual interaction.
    /// </summary>
    private void OnExecutionSummaryReady(object? sender, Plan plan)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var msgViewModel = new ChatMessageViewModel
            {
                Id = Guid.NewGuid().ToString(),
                Role = "assistant",
                Content = "",
                Timestamp = DateTime.Now,
                IsStreaming = false
            };

            var summaryCard = new ExecutionSummaryCardViewModel(plan);
            msgViewModel.Elements.Add(summaryCard);
            Messages.Add(msgViewModel);
            ScrollToBottom();
        });
    }

    private void OnStreamingUpdated(object? sender, ChatMessage msg)
    {
        // Stop countdown timer when streaming resumes successfully
        _retryCountdownTimer?.Stop();
        _retryCountdownTimer = null;
        
        Dispatcher.InvokeAsync(() =>
        {
            if (NetworkRetryBanner != null) NetworkRetryBanner.Visibility = Visibility.Collapsed;
            if (AutoContinueBanner != null) AutoContinueBanner.Visibility = Visibility.Collapsed;
        });

        lock (_streamUpdateLock)
        {
            _pendingStreamContent = msg.Content;
            if (_streamUpdateScheduled) return;
            _streamUpdateScheduled = true;
        }

        _ = Dispatcher.InvokeAsync(ApplyPendingStreamUpdate, DispatcherPriority.Render);
    }

    private void ApplyPendingStreamUpdate()
    {
        string? content;
        lock (_streamUpdateLock)
        {
            content = _pendingStreamContent;
            _pendingStreamContent = null;
            _streamUpdateScheduled = false;
        }

        if (content == null) return;

        for (int i = Messages.Count - 1; i >= 0; i--)
        {
            if (Messages[i].Role == "assistant" && Messages[i].IsStreaming)
            {
                // Compute only the new suffix since the last dispatched update so
                // AppendTextDelta mutates the last TextElement in-place rather than
                // rebuilding the whole content on every token tick.
                if (content.Length < _lastStreamedLength)
                {
                    _lastStreamedLength = 0;
                }

                if (content.Length > _lastStreamedLength)
                {
                    var delta = content.Substring(_lastStreamedLength);
                    _lastStreamedLength = content.Length;
                    Messages[i].AppendTextDelta(delta);
                }
                break;
            }
        }

        ScrollToBottom();
    }

    private void OnStreamingCompleted(object? sender, ChatMessage msg)
    {
        lock (_streamUpdateLock)
        {
            _pendingStreamContent = null;
            _streamUpdateScheduled = false;
        }

        _ = Dispatcher.InvokeAsync(async () =>
        {
            Plan? activePlan = null;
            if (_planService != null && _chatService?.ActiveSession != null)
            {
                activePlan = await _planService.GetActivePlanAsync(_chatService.ActiveSession.Id, default);
            }

            if (NetworkRetryBanner != null) NetworkRetryBanner.Visibility = Visibility.Collapsed;
            if (AutoContinueBanner != null) AutoContinueBanner.Visibility = Visibility.Collapsed;
            IsStreaming = false;
            StreamingProgressBar.Visibility = Visibility.Collapsed;
            ThinkingAnimationPanel.Visibility = Visibility.Collapsed;
            for (int i = Messages.Count - 1; i >= 0; i--)
            {
                if (Messages[i].Role == "assistant" && Messages[i].IsStreaming)
                {
                    var updated = Messages[i];
                    // Flush any remaining content that arrived between the last render tick
                    // and the completion event, then seal all open text segments so Markdown
                    // rendering is triggered exactly once at turn end.
                    if (msg.ContentBlocks?.Any(b => b.CompletionCallId != null) == true)
                    {
                        // Completion replaces the generation; length-based suffix logic can
                        // truncate or omit it. Use exactly the same trace as history restore.
                        updated.Elements.Clear();
                        updated._toolIndex.Clear();
                        RestoreToolTrace(updated, msg.ContentBlocks, activePlan);
                        updated.Content = msg.Content;
                    }
                    else 
                    {
                        if (msg.Content.Length < _lastStreamedLength)
                        {
                            _lastStreamedLength = 0;
                        }

                        if (msg.Content.Length > _lastStreamedLength)
                        {
                            var finalDelta = msg.Content.Substring(_lastStreamedLength);
                            updated.AppendTextDelta(finalDelta);
                        }
                    }
                    if (msg.WasCancelled && string.IsNullOrWhiteSpace(updated.Content))
                    {
                        updated.AppendTextDelta("_Generation stopped._");
                    }
                    updated.FinalizeStreaming();
                    _lastStreamedLength = 0;

                    // FAILSAFE (Tool status indicator): If any tool cards are still showing
                    // "Running..." when the full response completes, mark them done. This covers
                    // cases where the [TOOL_DONE] log entry was not received (e.g. logging
                    // disabled, or the tool raised an exception before returning a result).
                    foreach (var element in updated.Elements.OfType<AiAssistant.UI.ViewModels.ToolCallViewModel>())
                    {
                        if (element.IsRunning)
                        {
                            element.Complete(element.ResultText, msg.WasCancelled ? "Cancelled" : "Completed");
                        }
                    }

                    break;
                }
            }
            ScrollToBottom();

            // A plan proposed mid-turn is surfaced now, so the reviewer sees the agent's full
            // reasoning before the overlay covers the conversation.
            
            // Auto-continue: if the turn ended abnormally (not user-cancelled) and the last
            // assistant turn has unresolved tool calls, resume automatically.
            if (!msg.WasCancelled && !_userPressedStop && _settingsService?.AutoContinueOnInterrupt == true)
            {
                _ = CheckAndAutoContinueAsync();
            }
            
        }, DispatcherPriority.Render);
    }

    private async Task CheckAndAutoContinueAsync()
    {
        if (_chatService?.ActiveSession == null || _isStreaming) return;

        var hasStale = await _chatService.HasUnresolvedToolCallsAsync(_chatService.ActiveSession.Id);
        if (!hasStale) return;

        await Dispatcher.InvokeAsync(() =>
        {
            if (AutoContinueBanner != null && AutoContinueText != null)
            {
                AutoContinueText.Text = "⟳ Agent was interrupted mid-task — resuming automatically…";
                AutoContinueBanner.Visibility = Visibility.Visible;
            }
        });

        await System.Threading.Tasks.Task.Delay(1500);

        if (_isStreaming) return;

        await Dispatcher.InvokeAsync(async () =>
        {
            const string resumePrompt =
                "[SYSTEM: Your previous turn ended before all tool results were received. " +
                "The incomplete tool calls have been marked as failed with an error message. " +
                "Please continue the task from where you left off.]";

            await RunAgentTurnAsync(
                agentMessage: resumePrompt,
                originalPrompt: null,
                addUserBubble: false);
        });
    }

    private void DismissAutoContinueBanner_Click(object sender, RoutedEventArgs e)
    {
        if (AutoContinueBanner != null)
        {
            AutoContinueBanner.Visibility = Visibility.Collapsed;
        }
    }

    private AgentPause? _agentPause;
    private bool _agentTurnRunning;
    private bool _isAddingGuidance;

    private void ClearGuidanceMode()
    {
        if (!_isAddingGuidance) return;
        _isAddingGuidance = false;
        GuidanceHint.Visibility = Visibility.Collapsed;
        ComposerBorder.ClearValue(Border.BorderBrushProperty);
        ComposerBorder.ClearValue(Border.BorderThicknessProperty);
        UpdatePlaceholderVisibility();
    }

    private void ShowAgentPause(AgentPause? pause)
    {
        ClearGuidanceMode();
        _agentPause = pause;
        AgentPauseBanner.Visibility = pause == null ? Visibility.Collapsed : Visibility.Visible;
        PauseBody.Visibility = Visibility.Visible;
        PauseDetailsExpander.IsExpanded = false;
        PauseSummary.Text = pause?.Summary ?? "";
        PauseDetails.Text = pause?.Details ?? "";
        ContinueAgentButton.IsEnabled = pause != null && !_agentTurnRunning && !IsStreaming;
        PauseGuidanceButton.IsEnabled = ContinueAgentButton.IsEnabled;
        if (pause != null) ClearError();
    }

    private void OnAgentPaused(object? sender, AgentPause pause)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_chatService?.ActiveSession?.Id == pause.SessionId) ShowAgentPause(pause);
        });
    }

    private async void ContinueAgent_Click(object sender, RoutedEventArgs e)
    {
        var pause = _agentPause;
        if (pause == null || _agentTurnRunning || IsStreaming || _chatService?.ActiveSession?.Id != pause.SessionId) return;
        await RunAgentTurnAsync(pause.RecoveryPrompt, originalPrompt: "Continue");
    }

    private void PauseGuidance_Click(object sender, RoutedEventArgs e)
    {
        if (_agentPause == null || _agentTurnRunning || IsStreaming ||
            _chatService?.ActiveSession?.Id != _agentPause.SessionId) return;

        _isAddingGuidance = true;
        GuidanceHint.Visibility = Visibility.Visible;
        ComposerBorder.SetResourceReference(Border.BorderBrushProperty, "BorderFocusBrush");
        ComposerBorder.BorderThickness = new Thickness(2);
        UpdatePlaceholderVisibility();
        MessageTextBox.Focus();
        MessageTextBox.TextArea.Focus();
    }
    private void DismissPause_Click(object sender, RoutedEventArgs e)
    {
        PauseBody.Visibility = PauseBody.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnChatError(object? sender, string error)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (NetworkRetryBanner != null) NetworkRetryBanner.Visibility = Visibility.Collapsed;
            if (AutoContinueBanner != null) AutoContinueBanner.Visibility = Visibility.Collapsed;
            IsStreaming = false;
            StreamingProgressBar.Visibility = Visibility.Collapsed;
            ThinkingAnimationPanel.Visibility = Visibility.Collapsed;

            // Check if the error indicates a network issue
            if (IsNetworkErrorMessage(error))
            {
                ShowError("Internet connection lost. Please check your network and try again.");
                ConnectionStatus = "🔴 No internet";
            }
            else
            {
                ShowError(error);
            }

            UpdateConnectionStatus(); // Refresh status in case API key was removed
            _telemetryService?.TrackErrorAsync("chat_error", error);

            // If the turn failed after the plan tool ran, the plan is still valid — show it rather
            // than leaving the user with an error and no way to reach the proposal.
            
        });
    }

    private void OnRetryInitiated(object? sender, (TimeSpan Delay, string Reason) e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (NetworkRetryBanner == null || NetworkRetryText == null) return;

            // Cancel any existing countdown timer
            _retryCountdownTimer?.Stop();
            _retryCountdownTimer = null;
            
            // Calculate when the retry will happen
            _retryTargetTime = DateTime.Now.Add(e.Delay);
            _retryReason = e.Reason;
            
            // Show banner with initial countdown
            NetworkRetryBanner.Visibility = Visibility.Visible;
            NetworkRetryText.Text = $"Connection issue: {e.Reason}. Retrying in {e.Delay.TotalSeconds:F0}s...";
            
            // Start countdown timer (updates every second)
            _retryCountdownTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            
            _retryCountdownTimer.Tick += RetryCountdownTimer_Tick;
            _retryCountdownTimer.Start();
        });
    }

    private void RetryCountdownTimer_Tick(object? sender, EventArgs e)
    {
        if (NetworkRetryText == null)
        {
            _retryCountdownTimer?.Stop();
            return;
        }

        var remaining = _retryTargetTime - DateTime.Now;
        
        if (remaining.TotalSeconds <= 0)
        {
            // Retry should be happening now - stop countdown
            _retryCountdownTimer?.Stop();
            return;
        }
        
        // Update countdown text with remaining seconds
        NetworkRetryText.Text = $"Connection issue: {_retryReason}. Retrying in {remaining.TotalSeconds:F0}s...";
    }

    private void DismissNetworkRetryBanner_Click(object sender, RoutedEventArgs e)
    {
        // Stop countdown timer when user dismisses banner
        _retryCountdownTimer?.Stop();
        _retryCountdownTimer = null;
        
        if (NetworkRetryBanner != null)
        {
            NetworkRetryBanner.Visibility = Visibility.Collapsed;
        }
    }

    private void OnFileIndexed(object? sender, (string FileName, int Remaining) e)
    {
        var filePath = e.FileName;
        if (!string.IsNullOrEmpty(filePath))
        {
            // _skeletonCache.Invalidate(filePath); // Commenting out to fix build error
        }
    }

    private void OnTokenUsageChanged(object? sender, (int Current, int Max) usage)
    {
        Dispatcher.InvokeAsync(() =>
        {
            ContextUsagePanel.Visibility = usage.Max > 0 ? Visibility.Visible : Visibility.Collapsed;
            string Compact(int value) => value >= 1000000 ? $"{value / 1000000d:0.#}M" : value >= 1000 ? $"{value / 1000d:0.#}k" : value.ToString();
            TokenUsageText.Text = $"{Compact(usage.Current)} / {Compact(usage.Max)} · {(usage.Max > 0 ? 100d * usage.Current / usage.Max : 0):0.#}%";
            ContextUsagePanel.ToolTip = $"Context: {usage.Current:N0} / {usage.Max:N0} tokens. Provider usage when available; otherwise estimated.";
            TokenUsageProgressBar.Maximum = Math.Max(1, usage.Max);
            
            var animation = new System.Windows.Media.Animation.DoubleAnimation
            {
                To = Math.Max(0, Math.Min(usage.Current, usage.Max)),
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            TokenUsageProgressBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, animation);
            
            double percentage = (double)usage.Current / (usage.Max == 0 ? 1 : usage.Max);
            if (percentage > 0.9)
                TokenUsageProgressBar.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "ErrorRedBrush");
            else if (percentage > 0.75)
                TokenUsageProgressBar.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "WarningOrangeBrush");
            else
                TokenUsageProgressBar.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "AccentPrimaryBrush");
        });
    }

    private async void ChatPanel_Loaded(object sender, RoutedEventArgs e)
    {
        SetCheckpointError(_chatService?.CheckpointError ?? "");

        // A VS tool window can unload while hidden and later reuse the same control.
        // Restore these subscriptions before any awaited initialization so no tool event
        // is lost during panel startup.
        SubscribeToChatServiceEvents();
        SubscribeToToolLogBus();
        
        // Each Markdown viewer owns its document and renders when loaded.

        if (_approvalService != null)
        {
            _approvalService.ApprovalRequested -= OnApprovalRequested;
            _approvalService.ApprovalRequested += OnApprovalRequested;
        }

        // Initialize the History panel with services
        if (HistoryPanelControl != null)
        {
            HistoryPanelControl.SetServices(_chatService, _telemetryService);
        }

        // Check for update notification
        CheckForUpdateNotification();

        // Load theme preference is now handled globally by ThemeManager

        Helpers.ThemeManager.ThemeChanged -= OnThemeChanged;
        Helpers.ThemeManager.ThemeChanged += OnThemeChanged;

        if (_userPreferences != null)
        {
            _userPreferences.PreferencesChanged -= OnPreferencesChanged;
            _userPreferences.PreferencesChanged += OnPreferencesChanged;
            _uiPrefs = await _userPreferences.LoadAsync();
        }

        OnThemeChanged(null, EventArgs.Empty);

        // FIX (startup hang): These three loads are independent — run them concurrently
        // instead of sequentially to avoid stacking their individual I/O latencies.
        await Task.WhenAll(
            LoadProvidersAndModelsAsync(),
            LoadPersonasAsync(),
            LoadDatabaseConnectionsAsync()
        );
        await LoadSessionsAsync();
        await LoadActiveSessionMessagesAsync();
        
        if (_vsEnvService != null)
        {
            // Guard against double-subscription on repeated Loaded calls (e.g. returning from
            // a tab switch while streaming, where Unloaded returned early without unsubscribing).
            _vsEnvService.WorkspaceChanged -= OnWorkspaceChanged;
            _vsEnvService.WorkspaceChanged += OnWorkspaceChanged;
        }

        await ApplyWorkspaceRootAsync(await GetActiveWorkspacePathAsync());

        // Connection status is already updated inside LoadProvidersAndModelsAsync
        // Show welcome view if no sessions
        await UpdateWelcomeViewAsync();

        // Focus message box instead of comboboxes taking default focus
        _ = Dispatcher.InvokeAsync(() =>
        {
            MessageTextBox.Focus();
        }, DispatcherPriority.Input);

        MessageTextBox.TextArea.TextView.LineTransformers.Add(new AiAssistantPackage.MentionColorizer());
    }

    private void CheckForUpdateNotification()
    {
        // FIX: Task.Run should contain synchronous work, not async lambda
        _ = Task.Run(() =>
        {
            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var versionFile = Path.Combine(localAppData, AiAssistantPackage.PackageGuidString, "version.txt");
                var currentVersion = typeof(ChatPanel).Assembly.GetName().Version?.ToString() ?? "1.0.0";

                bool showUpdate = false;
                bool showWelcome = false;
                string? lastVersion = null;

                // FIX Feature 9: All File I/O already on background thread (Task.Run), no changes needed here.
                if (File.Exists(versionFile))
                {
                    lastVersion = File.ReadAllText(versionFile).Trim();
                    if (lastVersion != currentVersion)
                    {
                        showUpdate = true;
                    }
                }
                else
                {
                    showWelcome = true;
                }

                Dispatcher.InvokeAsync(() =>
                {
                    if (showUpdate)
                    {
                        UpdateNotificationText.Text = $"ApexCode updated to v{currentVersion}. See what's new!";
                        UpdateNotificationBanner.Visibility = Visibility.Visible;
                    }
                    else if (showWelcome)
                    {
                        UpdateNotificationText.Text = $"Welcome to ApexCode v{currentVersion}! Click Get Started to begin.";
                        UpdateNotificationBanner.Visibility = Visibility.Visible;
                    }
                });

                // Write current version
                var dir = Path.GetDirectoryName(versionFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                AiAssistant.Storage.SafeFileWriter.WriteAllText(versionFile, currentVersion);
            }
            catch (Exception ex)
            {
                // FIX Cross-cutting #1: Log the exception so silent failures are diagnosable.
                _outputLogger?.Log($"Update check failed: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[ApexCode] Update check failed: {ex.Message}");
            }
        });
    }

    private void DismissUpdateBanner_Click(object sender, RoutedEventArgs e)
    {
        UpdateNotificationBanner.Visibility = Visibility.Collapsed;
    }



    public void ApplyTheme(bool dark)
    {
        _isDarkTheme = dark;
        if (MainGrid == null) return;
        
        // Update the theme dictionary within this UserControl's resources
        if (this.Resources.MergedDictionaries.Count > 0)
        {
            for (int i = 0; i < this.Resources.MergedDictionaries.Count; i++)
            {
                var dict = this.Resources.MergedDictionaries[i];
                if (dict.Source != null && dict.Source.ToString().Contains("Theme.xaml"))
                {
                    this.Resources.MergedDictionaries[i] = new System.Windows.ResourceDictionary 
                    { 
                        Source = new Uri(dark ? "pack://application:,,,/AiAssistant.UI;component/Themes/DarkTheme.xaml" : "pack://application:,,,/AiAssistant.UI;component/Themes/LightTheme.xaml", UriKind.Absolute) 
                    };
                    break;
                }
            }
        }
    }

    private async Task UpdateWelcomeViewAsync()
    {
        if (_chatService == null) return;
        var sessions = await _chatService.GetSessionsAsync();
        var activeSession = _chatService.ActiveSession;

        if (activeSession == null || sessions == null || sessions.Count == 0 || Messages.Count == 0)
        {
            WelcomeViewControl.IsConfigured = await IsAnyProviderConfiguredAsync();
            WelcomeViewControl.Visibility = Visibility.Visible;
            MessagesList.Visibility = Visibility.Collapsed;
        }
        else
        {
            WelcomeViewControl.Visibility = Visibility.Collapsed;
            MessagesList.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Checks whether at least one provider has an API key configured (or is Ollama,
    /// which doesn't require one), so the WelcomeView can steer first-time users to
    /// Settings instead of letting them hit "No API key configured" after their first Send.
    /// </summary>
    private async Task<bool> IsAnyProviderConfiguredAsync()
    {
        if (_providerRepo == null) return true; // fail open — don't block onboarding on a missing repo

        try
        {
            var profiles = _cachedProfiles ?? await _providerRepo.GetAllAsync();
            return profiles.Any(p =>
                (p.ProviderType?.ToLowerInvariant() == "ollama") ||
                !string.IsNullOrEmpty(p.ApiKey));
        }
        catch (Exception ex)
        {
            _outputLogger?.Log($"Failed to check provider configuration for onboarding: {ex.Message}");
            return true; // fail open — a transient error shouldn't block the welcome flow
        }
    }

    private void WelcomeView_GetStarted(object? sender, EventArgs e)
    {
        MessageTextBox.Focus();
    }

    private void WelcomeView_ConfigureApiKeyRequested(object? sender, EventArgs e)
    {
        // FIX (onboarding): route straight to Settings instead of letting the user
        // type a first message and only find out about the missing API key after
        // hitting Send.
        _telemetryService?.TrackEventAsync("settings_opened", "welcome_configure_api_key");
        ShowSettingsPanel();
        SettingsViewControl?.SelectProviderTab();
    }

    /// <summary>
    /// Checks if the currently selected provider has a valid API key configured.
    /// Shows an error and returns false if no key is found.
    /// </summary>
    private async Task<bool> ValidateApiKeyAsync()
    {
        if (_providerRepo == null)
        {
            ShowError("Provider repository is not available. Please restart Visual Studio.");
            return false;
        }

        var selectedProviderId = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (string.IsNullOrEmpty(selectedProviderId)) return false;

        try
        {
            // Use cached profiles if available, otherwise fetch (with timeout protection)
            var profiles = _cachedProfiles ?? await _providerRepo.GetAllAsync();
            var profile = profiles.FirstOrDefault(p => p.Id == selectedProviderId);

            if (profile != null && profile.ProviderType?.ToLowerInvariant() == "ollama")
                return true;

            if (profile == null || string.IsNullOrEmpty(profile.ApiKey))
            {
                var name = profile?.Name ?? "Selected Provider";
                ShowError($"No API key configured for '{name}'. Please open Settings (gear icon) and add an API key.");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            if (IsNetworkException(ex) || !NetworkInterface.GetIsNetworkAvailable())
            {
                ShowError("Internet connection lost. Please check your network settings.");
            }
            else
            {
                ShowError($"Failed to check API key: {ex.Message}");
            }
            return false;
        }
    }

    private void OnProvidersChanged(object? sender, EventArgs e)
    {
        _ = LoadProvidersAndModelsAsync();
    }

    private async Task LoadProvidersAndModelsAsync()
    {
        if (_providerRepo == null) return;
        try
        {
            var allProfiles = await _providerRepo.GetAllAsync();
            var profiles = allProfiles.Where(p => p.IsEnabled && (!string.IsNullOrWhiteSpace(p.ApiKey) || p.ProviderType?.ToLowerInvariant() == "ollama")).ToList();
            _cachedProfiles = profiles;
            await Dispatcher.InvokeAsync(async () =>
            {
                var previousSelectionId = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                ProviderCombo.Items.Clear();
                
                ComboBoxItem? itemToSelect = null;
                foreach (var p in profiles)
                {
                    var item = new ComboBoxItem { Content = p.Name, Tag = p.Id };
                    ProviderCombo.Items.Add(item);
                    if (previousSelectionId != null && p.Id == previousSelectionId)
                        itemToSelect = item;
                }

                if (ProviderCombo.Items.Count > 0)
                {
                    if (itemToSelect != null)
                        ProviderCombo.SelectedItem = itemToSelect;
                    else
                        ProviderCombo.SelectedIndex = 0;

                    // Update connection status after selection is set
                    UpdateConnectionStatusInternal(profiles);
                }
                else
                {
                    ConnectionStatus = "⚪ Not configured";
                }
                
                await UpdateWelcomeViewAsync();
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load providers: {ex.Message}");
        }
    }


    private async Task LoadDatabaseConnectionsAsync()
    {
        try
        {
            if (_dbService == null) return;

            var connections = await _dbService.GetAllConnectionsAsync();
            var activeConnectionId = _dbService.ActiveConnection?.Id;

            await Dispatcher.InvokeAsync(() =>
            {
                var previousSelectionId = (DatabaseCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? activeConnectionId;
                DatabaseCombo.Items.Clear();

                var defaultItem = new ComboBoxItem { Content = "No DB connection selected", Tag = "" };
                DatabaseCombo.Items.Add(defaultItem);

                ComboBoxItem? itemToSelect = null;
                foreach (var conn in connections)
                {
                    var item = new ComboBoxItem { Content = conn.Name, Tag = conn.Id };
                    DatabaseCombo.Items.Add(item);
                    
                    if (previousSelectionId != null && conn.Id == previousSelectionId)
                    {
                        itemToSelect = item;
                    }
                }

                if (itemToSelect != null)
                    DatabaseCombo.SelectedItem = itemToSelect;
                else
                    DatabaseCombo.SelectedIndex = 0;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => ShowError($"Failed to load database connections: {ex.Message}"));
        }
    }

    private async Task LoadPersonasAsync()
    {
        try
        {
            if (_personaService == null) return;

            var personas = await _personaService.GetAllPersonasAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                PersonaCombo.Items.Clear();
                foreach (var persona in personas)
                {
                    PersonaCombo.Items.Add(new ComboBoxItem
                    {
                        Content = persona.Name,
                        Tag = persona.Id,
                        IsSelected = persona.IsDefault
                    });
                }
                if (PersonaCombo.Items.Count > 0)
                    PersonaCombo.SelectedIndex = 0;

                // Show active persona badge
                var activePersona = _personaService.ActivePersona;
                if (activePersona != null)
                {
                    PersonaBadge.Visibility = Visibility.Visible;
                    PersonaBadgeText.Text = activePersona.Name;
                }
                
                // Scroll to bottom after loading
                if (VisualTreeHelper.GetChildrenCount(MessagesList) > 0 && VisualTreeHelper.GetChild(MessagesList, 0) is Decorator border)
                {
                    if (border.Child is ScrollViewer scrollViewer)
                    {
                        scrollViewer.ScrollToEnd();
                    }
                }
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => ShowError($"Failed to load personas: {ex.Message}"));
        }
    }

    private async void PersonaCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (PersonaCombo.SelectedItem is not ComboBoxItem item || item.Tag == null) return;
            var personaId = item.Tag.ToString();
            if (string.IsNullOrEmpty(personaId)) return;

            if (_personaService != null)
            {
                await _personaService.SetActivePersonaAsync(personaId);
                var activePersona = _personaService.ActivePersona;
                if (activePersona != null)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        PersonaBadge.Visibility = Visibility.Visible;
                        PersonaBadgeText.Text = activePersona.Name;
                    });
                    _telemetryService?.TrackPersonaUsedAsync(activePersona.Name);
                }
            }
            else
            {
                await Dispatcher.InvokeAsync(() => ShowError("Persona service is not available yet. Please wait for initialization to complete."));
            }
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => ShowError($"Failed to set persona: {ex.Message}"));
        }
    }

    private async void DatabaseCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // FIX: Activate the selected database connection so DB tools (GetDatabaseSchemaFunction,
        // DatabaseQueryToolExecutor) can read _connectionService.ActiveConnection. Previously this
        // was a no-op placeholder, so every DB tool call returned "No active database connection."
        if (DatabaseCombo.SelectedItem is not ComboBoxItem item || item.Tag?.ToString() is not string connectionId)
            return;
        if (_dbService == null) return;

        try
        {
            if (string.IsNullOrEmpty(connectionId))
            {
                _dbService.ClearActiveConnection();
            }
            else
            {
                await _dbService.SetActiveConnectionAsync(connectionId);
            }
        }
        catch (Exception ex)
        {
            ShowError($"Failed to activate database connection: {ex.Message}");
        }
    }

    private string? _loadedSessionId;

    private async Task LoadActiveSessionMessagesAsync(bool forceReload = false)
    {
        try
        {
            if (_chatService == null)
                return;

            var session = _chatService.ActiveSession;
            if (session == null) return;

            if (!forceReload && _loadedSessionId == session.Id)
                return;

            _loadedSessionId = session.Id;

            await Dispatcher.InvokeAsync(() =>
            {
                if (_taskExecutionViewModel?.Plan?.SessionId != session.Id && _taskExecutionViewModel != null)
                    _taskExecutionViewModel.Plan = null;
            });

            // SQLite and content reconstruction can complete synchronously. Keep that
            // work off the dispatcher so the conversation loading ring can animate.
            var messages = await Task.Run(() => _chatService.GetSessionMessagesAsync(session.Id));
            var savedPause = await _chatService.GetPausedTurnAsync(session.Id);
            
            var sessionCheckpoints = _checkpointService != null 
                ? await _checkpointService.GetCheckpointsForSessionAsync(session.Id) 
                : new List<CheckpointInfo>();
            var checkpointLookup = sessionCheckpoints.Where(c => c.Description == "Before AI Turn")
                .GroupBy(c => c.MessageId).ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.CreatedAt).First().Id);

            // FIX (startup hang): Fetch the active plan on the background thread here, BEFORE
            // entering Dispatcher.InvokeAsync. The old code called .GetAwaiter().GetResult()
            // inside the UI-thread callback, which deadlocks under the VS JTF scheduler.
            var activePlan = _planService != null
                ? await _planService.GetActivePlanAsync(session.Id)
                : null;

            await Dispatcher.InvokeAsync(() =>
            {
                if (_chatService.ActiveSession?.Id != session.Id) return;
                if (!string.IsNullOrEmpty(session.ActiveMode))
                {
                    _chatService.ActiveMode = session.ActiveMode;
                    if (ModeCombo != null)
                    {
                        foreach (var item in ModeCombo.Items)
                        {
                            if (item is ComboBoxItem comboItem && comboItem.Tag as string == session.ActiveMode)
                            {
                                ModeCombo.SelectedItem = comboItem;
                                break;
                            }
                        }
                    }
                }

                if (_taskExecutionViewModel != null)
                    _taskExecutionViewModel.Plan = activePlan;

                ShowAgentPause(savedPause);
                Messages.Clear();
                foreach (var msg in messages)
                {
                    var localTime = msg.Timestamp.Kind == DateTimeKind.Local ? msg.Timestamp : 
                        DateTime.SpecifyKind(msg.Timestamp, DateTimeKind.Utc).ToLocalTime();

                    var content = msg.Role == "user" ? (msg.OriginalPrompt ?? HistoryPanel.ExtractUserPrompt(msg.Content, msg.Content)) : msg.Content;
                    var viewModel = new ChatMessageViewModel
                    {
                        Role = msg.Role,
                        Content = content,
                        Timestamp = localTime,
                        HasCheckpoint = checkpointLookup.ContainsKey(msg.Id),
                        CheckpointId = checkpointLookup.TryGetValue(msg.Id, out var cpId) ? cpId : null,
                        IsStreaming = msg.IsStreaming
                    };
                    
                    if (msg.Role != "assistant")
                    {
                        viewModel.AppendTextBlock(content);
                    }
                    else
                    {
                        RestoreToolTrace(viewModel, msg.ContentBlocks, activePlan);
                        
                        if (msg.IsStreaming)
                        {
                            int prefixLen = 0;
                            if (msg.ContentBlocks != null)
                            {
                                foreach (var b in msg.ContentBlocks)
                                {
                                    if (b.Text != null && b.Role == "assistant") prefixLen += b.Text.Length;
                                }
                            }
                            
                            if (msg.Content != null && msg.Content.Length > prefixLen)
                            {
                                string missing = msg.Content.Substring(prefixLen);
                                var lastTextEl = viewModel.Elements.LastOrDefault() as TextElement;
                                if (lastTextEl != null)
                                {
                                    lastTextEl.AppendRaw(missing);
                                    lastTextEl.IsStreaming = true;
                                }
                                else
                                {
                                    var el = new TextElement { IsStreaming = true };
                                    el.AppendRaw(missing);
                                    viewModel.Elements.Add(el);
                                }
                            }
                            
                            _lastStreamedLength = msg.Content?.Length ?? 0;
                        }
                        else if ((msg.ContentBlocks == null || msg.ContentBlocks.Count == 0) && !string.IsNullOrEmpty(msg.Content))
                        {
                            viewModel.AppendTextBlock(msg.Content);
                        }
                    }
                    
                    Messages.Add(viewModel);
                }

                // The Enhanced Plan System handles its own state persistence via EnhancedPlanOrchestrator
                // activePlan is fetched on the background thread BEFORE entering Dispatcher.InvokeAsync
                // (see the await below this lambda) to avoid blocking the UI thread with
                // .GetAwaiter().GetResult() — a pattern that deadlocks under the VS JTF scheduler.
                if (_planService != null && activePlan != null)
                {
                    bool isRealPlan = !string.IsNullOrEmpty(activePlan.Summary) || activePlan.ImportantChanges?.Count > 0 || activePlan.ModifiedFiles?.Count > 0 || activePlan.Tasks?.Count > 0;

                    if (isRealPlan)
                    {
                        var msgViewModel = new ChatMessageViewModel
                        {
                            Id = Guid.NewGuid().ToString(),
                            Role = "assistant",
                            Content = "",
                            Timestamp = DateTime.SpecifyKind(activePlan.CreatedAtUtc, DateTimeKind.Utc).ToLocalTime(),
                            IsStreaming = false
                        };
                        
                        var planCard = new PlanProposedCardViewModel(activePlan, () => ApprovePlanForExecutionAsync(activePlan), (feedback) => {
                            if (!string.IsNullOrWhiteSpace(feedback) && _chatService != null && _chatService.ActiveSession != null)
                            {
                                var prompt = $"I have some feedback on the proposed plan:\n\n{feedback}\n\nPlease revise the plan using the revise_plan tool.";
                                _ = _chatService.SendMessageAsync(_chatService.ActiveSession.Id, prompt);
                            }
                        });
                        
                        msgViewModel.Elements.Add(planCard);
                        
                        int insertIndex = Messages.Count;
                        while (insertIndex > 0 && Messages[insertIndex - 1].Timestamp > msgViewModel.Timestamp)
                        {
                            insertIndex--;
                        }
                        Messages.Insert(insertIndex, msgViewModel);
                    }

                    // 1. Restore Question Cards state
                    bool hasPendingQuestion = false;
                    
                    foreach (var question in activePlan.OpenQuestions)
                    {
                        if (question.State == QuestionState.Pending)
                            hasPendingQuestion = true;
                    }

                    // 2. Restore _isAwaitingAnswer
                    if (hasPendingQuestion)
                    {
                        _isAwaitingAnswer = true;
                        ComposerBorder.BorderBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#EDA200")!;
                        ComposerBorder.BorderThickness = new System.Windows.Thickness(2);
                        UpdatePlaceholderVisibility();
                    }

                    // 3. Restore Execution Summary and Walkthrough
                    if (activePlan.Status == PlanStatus.Completed)
                    {
                        var summaryTimestamp = Messages.Count > 0 ? Messages.Last().Timestamp : DateTime.UtcNow;
                        
                        var summaryMsg = new ChatMessageViewModel
                        {
                            Id = Guid.NewGuid().ToString(),
                            Role = "assistant",
                            Content = "",
                            Timestamp = summaryTimestamp,
                            IsStreaming = false
                        };
                        var summaryCard = new ExecutionSummaryCardViewModel(activePlan);
                        summaryMsg.Elements.Add(summaryCard);
                        Messages.Add(summaryMsg);

                        if (activePlan.ImportantChanges.Count > 0 || activePlan.Decisions.Count > 0 || activePlan.Risks.Count > 0 || activePlan.ModifiedFiles.Count > 0)
                        {
                            var walkthroughMsg = new ChatMessageViewModel
                            {
                                Id = Guid.NewGuid().ToString(),
                                Role = "assistant",
                                Content = "",
                                Timestamp = summaryTimestamp.AddMilliseconds(1),
                                IsStreaming = false
                            };
                            var walkthroughCard = new WalkthroughCardViewModel(activePlan);
                            walkthroughMsg.Elements.Add(walkthroughCard);
                            Messages.Add(walkthroughMsg);
                        }
                    }
                }
                
                // Scroll to bottom after layout updates
                Dispatcher.InvokeAsync(() =>
                {
                    ScrollToBottom();
                }, DispatcherPriority.Loaded);

                if (_settingsService?.AutoContinueOnInterrupt == true)
                {
                    _ = CheckAndAutoContinueAsync();
                }
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => ShowError($"Failed to load messages: {ex.Message}"));
        }
    }

    private void RestoreToolTrace(ChatMessageViewModel message, IReadOnlyList<ContentBlock>? blocks, Plan? activePlan = null)
    {
        if (blocks == null || blocks.Count == 0) return;
        var completionIds = new HashSet<string>(StringComparer.Ordinal);

        // Walk the persisted blocks in their original chronological order.
        // The serializer stores them as [TextContent, FunctionCallContent,
        // FunctionResultContent, TextContent, …] which is exactly the execution
        // order — no guessing needed.
        foreach (var block in blocks)
        {
            if (block.Text != null && block.Role == "assistant")
            {
                if (block.CompletionCallId != null && !completionIds.Add(block.CompletionCallId)) continue;
                // Text segment — merge adjacent blocks from the same role.
                message.AppendTextBlock(block.Text);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(block.Name))
            {
                // Tool call start.
                var vm = message.AppendHistoricalTool(
                    block.CallId ?? Guid.NewGuid().ToString(),
                    block.Name!,
                    block.Arguments);
                vm.StatusText = message.IsStreaming ? "Running" : "Interrupted";
                
                if (block.Name == "ask_question" && activePlan?.OpenQuestions != null && block.Arguments != null && block.Arguments.TryGetValue("question", out var qObj) && qObj != null)
                {
                    string qText = qObj.ToString() ?? "";
                    // Support matching even if the JSON parsing slightly differs
                    var question = activePlan.OpenQuestions.FirstOrDefault(q => q.Question != null && (q.Question == qText || q.Question.Contains(qText) || qText.Contains(q.Question)));
                    
                    if (question != null)
                    {
                        var task = activePlan.Tasks?.FirstOrDefault(t => t.Id == question.TaskId);
                        var taskTitle = task?.Title ?? "Unknown Task";
                        var card = new QuestionCardViewModel(question, taskTitle, async (result) => 
                        {
                            if (_planService != null && _chatService?.ActiveSession != null)
                            {
                                await _planService.AnswerQuestionAsync(_chatService.ActiveSession.Id, activePlan.Id, question.Id, result);
                            }
                            
                            Dispatcher.Invoke(() => 
                            {
                                _isAwaitingAnswer = false;
                                ComposerBorder.BorderBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#33FFFFFF")!;
                                ComposerBorder.BorderThickness = new Thickness(1);
                                UpdatePlaceholderVisibility();
                            });
                        });
                        message.Elements.Add(card);
                    }
                }
                
                continue;
            }

            if (block.Result != null)
            {
                // Tool result — pair with the matching call by ID, or fall back to
                // the last open card for the same role.
                ToolCallViewModel? matchingCall = null;
                if (!string.IsNullOrWhiteSpace(block.CallId))
                    message._toolIndex.TryGetValue(block.CallId!, out matchingCall);
                matchingCall ??= message.Elements
                    .OfType<ToolCallViewModel>()
                    .LastOrDefault(t => !t.HasResult);

                if (matchingCall != null)
                {
                    var status = block.Result.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "Failed" : "Completed";
                    matchingCall.Complete(block.Result, status);
                    matchingCall.IsExpanded = matchingCall.HasChangePreview;
                }
            }
        }
    }


    // AddOrMergeToolCall has been removed — the new API uses
    // ChatMessageViewModel.AppendTool (for live calls) and
    // ChatMessageViewModel.AppendHistoricalTool (for restore).
    // Both register entries in the _toolIndex for O(1) completion lookup.

    private async Task LoadSessionsAsync()
    {
        try
        {
            if (_chatService == null) return;
            await Dispatcher.InvokeAsync(() =>
            {
                _ = UpdateWelcomeViewAsync();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => ShowError($"Failed to load sessions: {ex.Message}"));
        }
    }


    private static bool TrySetResource(ResourceDictionary root, string key, object? value)
    {
        if (root == null) return false;
        root[key] = value;
        return true;
    }

private void OnPreferencesChanged(object? sender, UICustomizationPreferences e)
    {
        _uiPrefs = e;
        double appFont   = GetBaseFontSize("BaseAppFontSize")     + e.AppFontSizeOffset;
        double msgFont   = GetBaseFontSize("BaseMessageFontSize") + e.MessageFontSizeOffset;
        double inputFont = GetBaseFontSize("BaseInputFontSize")   + e.InputFontSizeOffset;
        double codeFont  = GetBaseFontSize("BaseCodeFontSize")    + e.CodeFontSizeOffset;
        // Captions track the message offset so timestamps and chip labels stay in
        // proportion with the conversation text they annotate.
        double smallFont = GetBaseFontSize("BaseSmallFontSize")   + e.MessageFontSizeOffset;

        var app = System.Windows.Application.Current.Resources;
        app["AppFontSizeOverride"]    = appFont;
        app["MessageFontSizeOverride"] = msgFont;
        app["InputFontSizeOverride"]  = inputFont;
        app["CodeFontSizeOverride"]   = codeFont;
        app["SmallFontSizeOverride"]  = smallFont;

        if (!string.IsNullOrEmpty(e.AppFontFamily))
            app["AppFont"] = new System.Windows.Media.FontFamily(e.AppFontFamily);
        if (!string.IsNullOrEmpty(e.CodeFontFamily))
            app["CodeFont"] = new System.Windows.Media.FontFamily(e.CodeFontFamily);

        var container = Helpers.ThemeManager.GetThemeDictionaryContainer();
        TrySetResource(container, "AppFontSizeOverride",    appFont);
        TrySetResource(container, "MessageFontSizeOverride", msgFont);
        TrySetResource(container, "InputFontSizeOverride",  inputFont);
        TrySetResource(container, "CodeFontSizeOverride",   codeFont);
        TrySetResource(container, "SmallFontSizeOverride",  smallFont);
        if (!string.IsNullOrEmpty(e.AppFontFamily))
            TrySetResource(container, "AppFont", new System.Windows.Media.FontFamily(e.AppFontFamily));
        if (!string.IsNullOrEmpty(e.CodeFontFamily))
            TrySetResource(container, "CodeFont", new System.Windows.Media.FontFamily(e.CodeFontFamily));

        // Refresh font sizes on all message view models
        foreach (var msg in Messages)
        {
            msg.RefreshFontSize();
        }
    }

    private static double GetBaseFontSize(string key)
    {
        try
        {
            var container = Helpers.ThemeManager.GetThemeDictionaryContainer();
            foreach (System.Windows.ResourceDictionary dict in container.MergedDictionaries)
            {
                if (dict.Contains(key) && dict[key] is double d)
                    return d;
            }
        }
        catch { }
        return key switch
        {
            "BaseAppFontSize" => 13,
            "BaseMessageFontSize" => 11,
            "BaseInputFontSize" => 12,
            "BaseCodeFontSize" => 11,
            "BaseSmallFontSize" => 11,
            _ => 11
        };
    }

    private UICustomizationPreferences? _uiPrefs;

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        var dict = Helpers.ThemeManager.GetThemeDictionaryContainer();
        bool isDark = Helpers.ThemeManager.IsDarkTheme;
        
        // Re-apply saved preferences into the fresh theme container
        if (_uiPrefs != null)
        {
            OnPreferencesChanged(sender, _uiPrefs);
        }
        
        for (int i = this.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            var current = this.Resources.MergedDictionaries[i];
            if (current.Contains("AiAssistantThemeMarker") || (current.Source != null && current.Source.ToString().Contains("Theme.xaml")))
            {
                this.Resources.MergedDictionaries.RemoveAt(i);
            }
        }
        
        // Add the direct theme dictionary to force WPF DynamicResource invalidation
        this.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary 
        { 
            Source = new Uri(isDark ? "pack://application:,,,/AiAssistant.UI;component/Themes/DarkTheme.xaml" : "pack://application:,,,/AiAssistant.UI;component/Themes/LightTheme.xaml", UriKind.Absolute) 
        });
        
        this.Resources.MergedDictionaries.Add(dict);
    }

    private System.Threading.CancellationTokenSource? _workspaceRefreshCts;
    private string? _currentWatchedDirectory;

    private void ChatPanel_Unloaded(object sender, RoutedEventArgs e)
    {
        // Flush plan review state so comments typed but not yet submitted survive the tool window
        // being hidden and reopened.
        _ = Task.CompletedTask;

        // When the LLM is actively streaming we must NOT touch the streaming message state
        // and must NOT unsubscribe service events. The generation continues in the background
        // while the tab is hidden; all event handlers (OnStreamingUpdated, OnStreamingCompleted,
        // OnToolExecuting, OnToolCompleted, MessageReceived, etc.) must stay active so the
        // agent can complete its work and the UI picks up all results when the tab is shown again.
        //
        // IMPORTANT: Do NOT call FinalizeStreaming() here. It clears the IsStreaming flag on
        // the ChatMessageViewModel, which causes OnStreamingUpdated and OnStreamingCompleted
        // to silently skip the message (they filter by IsStreaming == true). All remaining
        // streamed content and tool-call results are then dropped while the background agent
        // keeps running, pushing the AI into error-recovery loops that produce repeated
        // "[Tool call failed]" messages via OnChatMessageReceived.
        //
        if (IsStreaming)
            return;

        // FIX: Stop retry countdown timer when panel unloads
        _retryCountdownTimer?.Stop();
        _retryCountdownTimer = null;

        // CRITICAL FIX: Unsubscribe ALL event handlers to prevent memory leaks
        // Without this, ChatPanel instances accumulate in memory on every reload
        
        if (_approvalService != null)
        {
            _approvalService.ApprovalRequested -= OnApprovalRequested;
        }

        if (_logBus != null)
        {
            _logBus.EntryPublished -= LogBus_EntryPublished;
        }

        // Unsubscribe theme & preferences
        Helpers.ThemeManager.ThemeChanged -= OnThemeChanged;
        if (_userPreferences != null)
        {
            _userPreferences.PreferencesChanged -= OnPreferencesChanged;
        }
        if (_settingsService != null)
        {
            _settingsService.ProviderSettingsChanged -= OnSettingsChanged;
        }

        // Unsubscribe backup service
        if (_backupService != null)
        {
            _backupService.CanUndoChanged -= BackupService_CanUndoChanged;
        }

        // Unsubscribe indexing
        if (_indexingService != null)
        {
            _indexingService.FileIndexed -= OnFileIndexed;
        }

        // Unsubscribe provider changes
        if (_providerChangeNotifier != null)
        {
            _providerChangeNotifier.ProvidersChanged -= OnProvidersChanged;
        }

        // Unsubscribe chat service
        if (_chatService != null)
        {
            _chatService.MessageReceived -= OnChatMessageReceived;
            _chatService.StreamingUpdated -= OnStreamingUpdated;
            _chatService.StreamingCompleted -= OnStreamingCompleted;
            _chatService.ErrorOccurred -= OnChatError;
        _chatService.AgentPaused -= OnAgentPaused;
            _chatService.TokenUsageChanged -= OnTokenUsageChanged;
            _chatService.ToolExecuting -= OnToolExecuting;
            _chatService.ToolCompleted -= OnToolCompleted;
        if (_chatService is ICommandProgressSource oldCommands) oldCommands.CommandProgress -= OnCommandProgress;
            _chatService.CheckpointCreated -= OnCheckpointCreated;
            _chatService.RetryInitiated -= OnRetryInitiated;
            _chatService.EnhancedPlanProposed -= OnEnhancedPlanProposed;
            _chatService.EnhancedQuestionAsked -= OnEnhancedQuestionAsked;
            _chatService.EnhancedWalkthroughGenerated -= OnEnhancedWalkthroughGenerated;
            _chatService.EnhancedPlanUpdated -= OnEnhancedPlanUpdated;
            _chatService.ScrollToActivePlanRequested -= OnScrollToActivePlanRequested;
            _chatService.ExecutionSummaryReady -= OnExecutionSummaryReady;
        }

        // Unsubscribe diff service
        if (_diffService != null)
        {
            // Diff popup removed per user request
        }

        // Unsubscribe workspace changes
        if (_vsEnvService != null)
        {
            _vsEnvService.WorkspaceChanged -= OnWorkspaceChanged;
        }

        // P0-2: State Machine Teardown
        UnloadedTeardown();

        // We intentionally DO NOT cancel _generationCts here.
        // Canceling it here breaks streaming when the user switches tabs.
    }

    private void OnWorkspaceChanged(object? sender, EventArgs e)
    {
        // FIX (startup hang): Never call .GetAwaiter().GetResult() on the UI thread inside
        // a VS extension — GetWorkspaceRootAsync() may need the UI thread's JTF context,
        // causing a deadlock that resolves only after a ~15-second timeout.
        // Use an async lambda in Dispatcher.InvokeAsync instead.
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (_vsEnvService == null) return;
            var root = await _vsEnvService.GetWorkspaceRootAsync();
            await ApplyWorkspaceRootAsync(root, forceRefresh: true);
        });
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(new Action(() => 
        {
            // Bounded staleness trigger: route through ApplyWorkspaceRootAsync forceRefresh to reuse identical pipeline
            _ = ApplyWorkspaceRootAsync(_activeWorkspacePath, forceRefresh: true);
        }));
    }

    private bool _rootSwitchInProgress;
    private (string? Root, bool Force)? _pendingRootRequest;
    private System.Threading.CancellationTokenSource? _enumerationCts;

    private async Task ApplyWorkspaceRootAsync(string? newRoot, bool forceRefresh = false)
    {
        // E2: UI-thread-affinity invariant. All state mutation requires UI thread.
        if (!Dispatcher.CheckAccess())
        {
            await await Dispatcher.InvokeAsync(() => ApplyWorkspaceRootAsync(newRoot, forceRefresh)); // H1: double await to observe exceptions
            return;
        }

        if (_rootSwitchInProgress)
        {
            // E1: last-writer-wins on Root; force flags OR-accumulate so intent is never dropped
            _pendingRootRequest = (newRoot, _pendingRootRequest?.Force == true || forceRefresh);
            return;
        }
        _rootSwitchInProgress = true;
        try
        {
            if (!forceRefresh && string.Equals(newRoot, _activeWorkspacePath, StringComparison.OrdinalIgnoreCase))
                return;

            _enumerationCts?.Cancel();
            _enumerationCts = null;
            _fileWatcher?.Dispose(); // Unified teardown site
            _fileWatcher = null;
            _workspaceFiles = System.Collections.Immutable.ImmutableList<string>.Empty;
            _activeWorkspacePath = newRoot;

            if (newRoot == null) { RenderNoWorkspaceState(); return; }
            if (IsTooBroadRoot(newRoot)) { RenderBroadRootState(newRoot); return; }

            var cts = new System.Threading.CancellationTokenSource();
            _enumerationCts = cts;
            // token captured locally — never re-read the field after await
            try
            {
                await StartWatcherAndEnumerateAsync(newRoot, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // H2: Expected: a newer switch superseded this enumeration. Swallow —
                // do NOT re-resolve; the newer request owns the state now.
            }
            catch (Exception ex)
            {
                _outputLogger?.Log($"[ApexCode] Enumeration failed for '{newRoot}': {ex.Message}");
                _ = ApplyWorkspaceRootAsync(newRoot);                      // recovery re-resolve; gate still held
            }
        }
        finally { _rootSwitchInProgress = false; }
        
        if (_pendingRootRequest is { } req) 
        { 
            _pendingRootRequest = null; 
            await ApplyWorkspaceRootAsync(req.Root, req.Force); 
        }
    }

    public void UnloadedTeardown()
    {
        _enumerationCts?.Cancel();
        _fileWatcher?.Dispose();
        _fileWatcher = null;
    }

    private void RenderNoWorkspaceState()
    {
        _outputLogger?.Log("[ApexCode] Workspace root is null; rendering no-workspace state.");
        // Surface the state through the same MentionListBox placeholder the popup uses
        // when the user opens "@" with no solution. No file enumeration runs, no
        // FileSystemWatcher is created, and tool invocations fail closed.
        try
        {
            if (MentionListBox != null)
            {
                MentionListBox.ItemsSource = new[] { MentionItem.Placeholder("No workspace is open. Open a solution or folder to enable file mentions.") };
            }
        }
        catch
        {
            // UI may already be torn down during Unloaded; render is best-effort.
        }
    }

    private void RenderBroadRootState(string root)
    {
        _outputLogger?.Log($"[ApexCode] Workspace root '{root}' is too broad (drive/UNC); skipping file enumeration.");
        try
        {
            if (MentionListBox != null)
            {
                MentionListBox.ItemsSource = new[] { MentionItem.Placeholder("Workspace root is a drive or network share; file enumeration is disabled for safety.") };
            }
        }
        catch
        {
            // UI may already be torn down during Unloaded; render is best-effort.
        }
    }

    private bool IsTooBroadRoot(string root)
    {
        if (string.IsNullOrEmpty(root)) return true;
        var normalized = Path.GetFullPath(root);
        return string.Equals(Path.GetPathRoot(normalized), normalized, StringComparison.OrdinalIgnoreCase);
    }

    private async Task StartWatcherAndEnumerateAsync(string directory, System.Threading.CancellationToken cancellationToken)
    {
        // 1. Setup Watcher (P0-3: Only Created, Deleted, Renamed)
        _fileWatcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            InternalBufferSize = 64 * 1024 // 64KB (default is 8KB)
        };

        _fileWatcher.Error += (sender, e) =>
        {
            var ex = e.GetException();
            _outputLogger?.Log($"[ApexCode] FileSystemWatcher error on '{directory}': {ex?.Message ?? "Buffer overflow"}. Triggering recovery re-resolve.");
            _ = _telemetryService?.TrackEventAsync("fsw_error", directory ?? "");

            Dispatcher.InvokeAsync(() => ApplyWorkspaceRootAsync(directory, forceRefresh: true));
        };

        _fileWatcher.Created += OnFsEvent;
        _fileWatcher.Deleted += OnFsEvent;
        _fileWatcher.Renamed += (s, e) => OnFsEvent(s, e);

        // 2. Perform Enumeration
        string[] extensions;
        if (_settingsService != null && !string.IsNullOrWhiteSpace(_settingsService.WorkspaceExtensions))
        {
            extensions = _settingsService.WorkspaceExtensions.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }
        else
        {
            extensions = new[] { ".cs", ".js", ".jsx", ".ts", ".tsx", ".py", ".c", ".cpp", ".cc", ".cxx", ".h", ".hpp", ".go", ".rs", ".java", ".rb", ".php", ".swift", ".kt", ".sh", ".ps1", ".sql", ".json", ".xml", ".yaml", ".yml", ".toml", ".md", ".txt" };
        }

        var files = await Task.Run(() =>
        {
            var result = new List<string>();
            var extensionSet = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
            var excludedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
            { 
                "obj", "bin", "node_modules", ".git", "__pycache__", ".vs", "packages", ".nuget", "dist", "build", "out" 
            };

            foreach (var file in SafeEnumerateFilesRecursive(directory, excludedDirs))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(file);
                if (!string.IsNullOrEmpty(ext) && extensionSet.Contains(ext))
                {
                    result.Add(file);
                    if (result.Count >= 20000) // P1: 20k cap
                    {
                        System.Diagnostics.Debug.WriteLine("[ApexCode] Workspace enumeration reached 20,000 files cap. Truncating.");
                        break;
                    }
                }
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }, cancellationToken).ConfigureAwait(false);

        await Dispatcher.InvokeAsync(() => 
        {
            if (cancellationToken.IsCancellationRequested) return;
            _workspaceFiles = System.Collections.Immutable.ImmutableList.CreateRange(files);
        });
    }

    private int _refreshQueued; // 0 = not queued
    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _refreshQueued, 1, 0) != 0) return;              
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            try 
            {
                if (!ReferenceEquals(sender, _fileWatcher)) return; // S32 handled inside UI thread
                _refreshQueued = 0;
                
                // Bounded staleness trigger: route through ApplyWorkspaceRootAsync forceRefresh to reuse identical pipeline
                _ = ApplyWorkspaceRootAsync(_activeWorkspacePath, forceRefresh: true);
            } 
            catch (Exception ex)
            {
                _outputLogger?.Log($"[ApexCode] Error during OnFsEvent UI dispatch: {ex.Message}");
            }
        }));
    }

    private static IEnumerable<string> SafeEnumerateFilesRecursive(string directory, HashSet<string> excludedDirNames)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory);
        }
        catch (UnauthorizedAccessException) { yield break; }
        catch (DirectoryNotFoundException) { yield break; }
        catch (IOException) { yield break; }

        foreach (var file in files)
        {
            yield return file;
        }

        IEnumerable<string> subDirs;
        try
        {
            subDirs = Directory.EnumerateDirectories(directory);
        }
        catch (UnauthorizedAccessException) { yield break; }
        catch (DirectoryNotFoundException) { yield break; }
        catch (IOException) { yield break; }

        foreach (var subDir in subDirs)
        {
            var dirInfo = new DirectoryInfo(subDir);
            if (dirInfo.Attributes.HasFlag(FileAttributes.Hidden)) continue; // P1: Hidden attribute check

            var dirName = dirInfo.Name;
            if (string.IsNullOrEmpty(dirName) || excludedDirNames.Contains(dirName))
            {
                continue; 
            }

            foreach (var file in SafeEnumerateFilesRecursive(subDir, excludedDirNames))
            {
                yield return file;
            }
        }
    }

    private async Task<string?> GetActiveWorkspacePathAsync()
    {
        if (_vsEnvService != null)
        {
            return await _vsEnvService.GetWorkspaceRootAsync();
        }
        return null;
    }

    private void MessageTextBox_TextChanged(object? sender, EventArgs e)
    {
        UpdatePlaceholderVisibility();

        if (IsStreaming) return;

        var text = MessageTextBox.Text;
        if (string.IsNullOrEmpty(text))
        {
            CloseMentionPopup();
            return;
        }

        var caretIndex = MessageTextBox.CaretOffset;
        if (caretIndex == 0)
        {
            CloseMentionPopup();
            return;
        }

        var textBeforeCaret = text.Substring(0, caretIndex);
        var atIndex = textBeforeCaret.LastIndexOf('@');
        if (atIndex < 0)
        {
            CloseMentionPopup();
            return;
        }

        if (atIndex > 0 && char.IsLetterOrDigit(textBeforeCaret[atIndex - 1]))
        {
            CloseMentionPopup();
            return;
        }

        var query = textBeforeCaret.Substring(atIndex + 1);
        if (query.Contains(' ') || query.Contains('\n') || query.Contains('\r'))
        {
            CloseMentionPopup();
            return;
        }

        _mentionTriggerIndex = atIndex;
        OpenMentionPopup(query);
    }

    /// <summary>
    /// The hint stays until there is actually something typed. Hiding it on focus alone
    /// left the composer looking like an empty grey slab, since clicking into the box is
    /// usually the first thing that happens.
    /// </summary>
    private void UpdatePlaceholderVisibility()
    {
        if (MessagePlaceholder == null || QaPlaceholderTextBlock == null) return;

        MessagePlaceholder.Text = _isAddingGuidance
            ? "Add instructions to help the agent continue…"
            : "Ask anything, @ to add a file";
        
        if (_isAwaitingAnswer && !_isAddingGuidance)
        {
            MessagePlaceholder.Visibility = Visibility.Collapsed;
            QaPlaceholderTextBlock.Visibility = string.IsNullOrEmpty(MessageTextBox.Text) 
                ? Visibility.Visible 
                : Visibility.Collapsed;
        }
        else
        {
            QaPlaceholderTextBlock.Visibility = Visibility.Collapsed;
            MessagePlaceholder.Visibility = string.IsNullOrEmpty(MessageTextBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void MessageTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        UpdatePlaceholderVisibility();
    }

    private void MessageTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        UpdatePlaceholderVisibility();
    }

    private async void OpenMentionPopup(string query)
    {
        var solutionDir = await GetActiveWorkspacePathAsync();
        
        if (string.IsNullOrEmpty(solutionDir))
        {
            MentionListBox.ItemsSource = new[] { MentionItem.Placeholder("Open a solution to enable file mentions") };
            MentionListBox.SelectedIndex = 0;
            MentionPopup.IsOpen = true;
            PositionMentionPopup();
            return;
        }

        var currentFiles = _workspaceFiles;
        var matches = await Task.Run(() => FuzzyMatchFiles(query, currentFiles, solutionDir, 12));

        // FIX (mention popup no-results): previously this silently closed the popup,
        // which looked identical to the extension having failed to notice the "@"
        // trigger at all. Keep the popup open with an explicit "No files found" row
        // so the user gets confirmation the search ran; it disappears normally as soon
        // as they keep typing and either match something or delete back past "@".
        if (matches.Count == 0)
        {
            MentionListBox.ItemsSource = new[] { MentionItem.Placeholder($"No files found matching \"{query}\"") };
            MentionListBox.SelectedIndex = 0;
            MentionPopup.IsOpen = true;
            PositionMentionPopup();
            return;
        }

        MentionListBox.ItemsSource = matches;
        MentionListBox.SelectedIndex = 0;
        MentionPopup.IsOpen = true;
        PositionMentionPopup();
    }

    private void CloseMentionPopup()
    {
        MentionPopup.IsOpen = false;
        _mentionTriggerIndex = -1;
    }

    private void PositionMentionPopup()
    {
        // FIX (mention popup position): Placement="Top" anchors the popup above a
        // PlacementRectangle instead of relying on manual VerticalOffset math against
        // Relative placement, which could place the popup on top of the input box when
        // the caret was near the bottom of a multi-line message. We compute a small
        // rectangle at the caret's line and let WPF handle flipping the popup above it.
        try
        {
            if (MessageTextBox.CaretOffset > 0)
            {
                var loc = MessageTextBox.Document.GetLocation(MessageTextBox.CaretOffset);
                var lineTop = MessageTextBox.TextArea.TextView.GetVisualPosition(new ICSharpCode.AvalonEdit.TextViewPosition(loc), ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineTop);
                var lineBottom = MessageTextBox.TextArea.TextView.GetVisualPosition(new ICSharpCode.AvalonEdit.TextViewPosition(loc), ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineBottom);
                var x = Math.Max(0, Math.Min(lineTop.X, MessageTextBox.ActualWidth - 220));
                MentionPopup.PlacementRectangle = new Rect(x, lineTop.Y, 1, lineBottom.Y - lineTop.Y);
            }
            else
            {
                // FIX (mention popup positioning): +4px so the popup doesn't sit flush
                // against the textbox border and look clipped when caret tracking isn't used.
                MentionPopup.PlacementRectangle = new Rect(0, 0, 1, MessageTextBox.ActualHeight + 4);
            }
        }
        catch
        {
            MentionPopup.PlacementRectangle = new Rect(0, 0, 1, MessageTextBox.ActualHeight + 4);
        }
    }

    private static List<MentionItem> FuzzyMatchFiles(string query, System.Collections.Immutable.ImmutableList<string> files, string? solutionDirectory, int maxResults)
    {
        if (string.IsNullOrEmpty(query))
        {
            return files.Take(maxResults).Select(f => new MentionItem(f, solutionDirectory)).ToList();
        }

        var scored = new List<(string File, int Score)>();
        var queryLower = query.ToLowerInvariant();

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var fileNameLower = fileName.ToLowerInvariant();
            
            string pathLower;
            if (!string.IsNullOrEmpty(solutionDirectory) && file.StartsWith(solutionDirectory, StringComparison.OrdinalIgnoreCase))
            {
                var relative = file.Substring(solutionDirectory.Length);
                if (relative.StartsWith("\\") || relative.StartsWith("/"))
                    relative = relative.Substring(1);
                pathLower = relative.ToLowerInvariant();
            }
            else
            {
                pathLower = file.ToLowerInvariant();
            }

            if (fileNameLower.Equals(queryLower))
            {
                scored.Add((file, 1000));
                continue;
            }

            if (fileNameLower.StartsWith(queryLower))
            {
                scored.Add((file, 500 + queryLower.Length));
                continue;
            }

            if (fileNameLower.Contains(queryLower))
            {
                scored.Add((file, 200 + queryLower.Length));
                continue;
            }
            
            if (pathLower.Contains(queryLower))
            {
                scored.Add((file, 100 + queryLower.Length));
                continue;
            }

            if (CharSequenceMatch(queryLower, pathLower, out var seqScore))
            {
                scored.Add((file, seqScore));
            }
        }

        // Sprint 4: Levenshtein distance fallback
        if (scored.Count == 0 && queryLower.Length >= 3)
        {
            foreach (var file in files)
            {
                var fileNameLower = Path.GetFileName(file).ToLowerInvariant();
                int dist = ComputeLevenshteinDistance(queryLower, fileNameLower);
                if (dist <= 2)
                {
                    scored.Add((file, 50 - dist)); // lower distance gets higher score
                }
            }
        }

        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.File)
            .Take(maxResults)
            .Select(x => new MentionItem(x.File, solutionDirectory))
            .ToList();
    }

    private static bool CharSequenceMatch(string query, string target, out int score)
    {
        score = 0;
        var qi = 0;
        var ti = 0;
        var matchStart = -1;
        var contiguous = 0;

        while (qi < query.Length && ti < target.Length)
        {
            if (query[qi] == target[ti])
            {
                if (qi == 0) matchStart = ti;
                qi++;
                contiguous++;
                score += 1 + contiguous;
            }
            else
            {
                contiguous = 0;
            }
            ti++;
        }

        if (qi == query.Length && matchStart >= 0)
        {
            score += (100 - matchStart);
            return true;
        }

        score = 0;
        return false;
    }

    private static int ComputeLevenshteinDistance(string source, string target)
    {
        if (string.IsNullOrEmpty(source)) return target?.Length ?? 0;
        if (string.IsNullOrEmpty(target)) return source.Length;

        int n = source.Length;
        int m = target.Length;
        var d = new int[n + 1, m + 1];

        for (int i = 0; i <= n; d[i, 0] = i++) { }
        for (int j = 0; j <= m; d[0, j] = j++) { }

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = (target[j - 1] == source[i - 1]) ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }
        return d[n, m];
    }

    private async void MentionListBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // If the click is on an item, SelectedItem will be set to a MentionItem.
        // If the click is on empty space, SelectedItem is null -> close the popup.
        if (MentionListBox.SelectedItem is not MentionItem)
        {
            CloseMentionPopup();
            MessageTextBox.Focus();
            return;
        }
        await SelectMentionAsync();
    }

    private void MentionPopup_Closed(object sender, EventArgs e)
    {
        // Ensure popup is fully closed and clean up any stale state
        _mentionTriggerIndex = -1;
    }

    private void MentionListBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Tab)
        {
            e.Handled = true;
            _selectMentionTask = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await SelectMentionAsync();
                }
                catch (Exception ex)
                {
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    System.Diagnostics.Debug.WriteLine($"[ApexCode] Mention selection failed: {ex}");
                    ShowError("Couldn't insert that file mention.");
                }
            });
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseMentionPopup();
            MessageTextBox.Focus();
        }
    }

    public class MentionItem
    {
        public string AbsolutePath { get; }
        public string RelativePath { get; }
        public string FileName { get; }
        public string DirectoryName { get; }

        /// <summary>
        /// True for informational rows ("No files found", "Open a solution...") that
        /// aren't real files and must not be selectable via Enter/Tab/click.
        /// </summary>
        public bool IsPlaceholder { get; private init; }

        public MentionItem(string absolutePath, string solutionDirectory)
        {
            AbsolutePath = absolutePath;
            FileName = Path.GetFileName(absolutePath);
            if (!string.IsNullOrEmpty(solutionDirectory) && absolutePath.StartsWith(solutionDirectory, StringComparison.OrdinalIgnoreCase))
            {
                var relative = absolutePath.Substring(solutionDirectory.Length);
                if (relative.StartsWith("\\") || relative.StartsWith("/"))
                    relative = relative.Substring(1);
                RelativePath = relative;
            }
            else
            {
                RelativePath = absolutePath;
            }
            
            var dirName = Path.GetDirectoryName(RelativePath);
            DirectoryName = string.IsNullOrEmpty(dirName) ? "" : dirName + "\\";
        }

        private MentionItem(string placeholderMessage)
        {
            AbsolutePath = placeholderMessage;
            RelativePath = placeholderMessage;
            FileName = placeholderMessage;
            DirectoryName = "";
            IsPlaceholder = true;
        }

        public static MentionItem Placeholder(string message) => new MentionItem(message);

        public override string ToString() => RelativePath;
    }

    private class MentionSpan : ICSharpCode.AvalonEdit.Document.TextSegment
    {
        public string AbsolutePath { get; }
        public string RelativePath { get; }

        public MentionSpan(string absolutePath, string relativePath)
        {
            AbsolutePath = absolutePath;
            RelativePath = relativePath;
        }
    }

    private async Task SelectMentionAsync()
    {
        if (MentionListBox.SelectedItem is not MentionItem item) return;
        if (item.IsPlaceholder)
        {
            // FIX (mention popup placeholder trap): previously returned here with the
            // popup still open, so Enter/click on "No files found" left the popup stuck
            // until the user hit Escape. Close it and return focus like a real selection.
            CloseMentionPopup();
            MessageTextBox.Focus();
            return;
        }
        var selectedFile = item.AbsolutePath;

        var triggerIndex = _mentionTriggerIndex;
        if (triggerIndex < 0) return;

        CloseMentionPopup();

        var caretIndex = MessageTextBox.CaretOffset;
        var insertText = "@" + item.RelativePath + " ";
        var lengthToReplace = caretIndex - triggerIndex;
        if (lengthToReplace < 0) lengthToReplace = 0;

        MessageTextBox.Document.Replace(triggerIndex, lengthToReplace, insertText);
        MessageTextBox.CaretOffset = triggerIndex + insertText.Length;

        try
        {
            // FIX Feature 9: File.Exists was blocking the UI thread. Move to Task.Run.
            bool fileExists = await Task.Run(() => File.Exists(selectedFile)).ConfigureAwait(true);
            if (fileExists)
            {
                _mentionSpans?.Add(new MentionSpan(selectedFile, item.RelativePath)
                {
                    StartOffset = triggerIndex,
                    Length = insertText.Length - 1 // Exclude the trailing space
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] Failed to track mentioned file {selectedFile}: {ex.Message}");
            _outputLogger?.Log($"Failed to track mentioned file {selectedFile}: {ex.Message}");
            ShowError("Couldn't insert that file mention.");
        }

        MessageTextBox.Focus();
    }

    // ── Drag & Drop: attach files from Solution Explorer ─────────────────────

    private void MessageTextBox_PreviewDragEnter(object sender, DragEventArgs e)
    {
        if (IsStreaming) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            // FIX (drag-drop visual cue): the OS "copy" cursor badge is easy to miss.
            // Show an in-panel highlighted border around the drop target too.
            DropTargetBorder.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void MessageTextBox_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (IsStreaming) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
            DropTargetBorder.Visibility = Visibility.Collapsed;
        }
        e.Handled = true;
    }

    private void MessageTextBox_PreviewDragLeave(object sender, DragEventArgs e)
    {
        DropTargetBorder.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    /// <summary>
    /// Intercepts Ctrl+V on the message input. If the clipboard holds a file-drop list
    /// (e.g. copied from File Explorer), routes it through the same attach flow used by
    /// drag-and-drop instead of falling through to AvalonEdit's default paste, which would
    /// otherwise just insert the raw file path as text. Plain text/other clipboard formats
    /// are left completely alone so normal paste behavior is unaffected.
    /// </summary>
    private async void MessageTextBox_PreviewPasteExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste) return;
        if (IsStreaming) return;

        string[]? files = null;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var dropList = Clipboard.GetFileDropList();
                files = new string[dropList.Count];
                dropList.CopyTo(files, 0);
            }
        }
        catch (Exception ex)
        {
            // Clipboard access can transiently fail (e.g. another process holding it).
            // Fall back to default paste behavior rather than swallowing the paste entirely.
            _outputLogger?.Log($"Failed to read clipboard for paste-to-attach: {ex.Message}");
            return;
        }

        if (files == null || files.Length == 0) return;

        // We're handling this ourselves — stop AvalonEdit's internal Paste binding from
        // also running and inserting the raw path text.
        e.Handled = true;

        var insertIndex = MessageTextBox.CaretOffset;
        await AttachDroppedFilesAsync(files, insertIndex);
    }

    private void MessageTextBox_DragEnter(object sender, DragEventArgs e)
    {
        // Tunnel event (PreviewDragEnter) already handled; this is unreachable
    }

    private void MessageTextBox_DragOver(object sender, DragEventArgs e)
    {
        // Tunnel event (PreviewDragOver) already handled; this is unreachable
    }

    private async void MessageTextBox_PreviewDrop(object sender, DragEventArgs e)
    {
        DropTargetBorder.Visibility = Visibility.Collapsed;

        if (IsStreaming) { e.Handled = true; return; }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Handled = true; return; }

        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files == null || files.Length == 0) { e.Handled = true; return; }

        e.Handled = true;

        // Determine drop position in the text from mouse coordinates
        var dropIndex = GetDropCaretIndex(e);

        await AttachDroppedFilesAsync(files, dropIndex);
    }

    private void MessageTextBox_Drop(object sender, DragEventArgs e)
    {
        // Tunnel event (PreviewDrop) already handled; this is unreachable
    }

    /// <summary>
    /// Maps the drag-drop mouse position to a caret index inside MessageTextBox.
    /// </summary>
    private int GetDropCaretIndex(DragEventArgs e)
    {
        try
        {
            var point = e.GetPosition(MessageTextBox.TextArea.TextView);
            var pos = MessageTextBox.TextArea.TextView.GetPosition(point);
            if (pos.HasValue)
            {
                return MessageTextBox.Document.GetOffset(pos.Value.Location);
            }
            return MessageTextBox.CaretOffset;
        }
        catch
        {
            return MessageTextBox.CaretOffset;
        }
    }

    /// <summary>
    /// Attaches dropped files to hidden context and inserts @filename references into the text.
    /// </summary>
    private async Task AttachDroppedFilesAsync(string[] files, int insertIndex)
    {
        // Filter to supported file extensions (same as mention feature)
        var extensions = new[] { ".cs", ".js", ".jsx", ".ts", ".tsx", ".py", ".c", ".cpp", ".cc", ".cxx", ".h", ".hpp", ".go", ".rs", ".java", ".rb", ".php", ".swift", ".kt", ".sh", ".ps1", ".sql", ".json", ".xml", ".yaml", ".yml", ".toml", ".md", ".txt" };

        var validFiles = await Task.Run(() =>
        {
            return files
                .Where(f => !Directory.Exists(f)) // skip directories
                .Where(f => extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                .Distinct()
                .ToList();
        });

        if (validFiles.Count == 0)
        {
            ShowError("No supported files in drop. Supported: code & text files.");
            return;
        }

        // Build the insertion text: "@file1 @file2 ..."
        var solutionDir = await GetActiveWorkspacePathAsync();
        var fileRefs = validFiles.Select(f => {
            var mention = new MentionItem(f, solutionDir);
            return "@" + mention.RelativePath;
        }).ToArray();
        var insertText = string.Join(" ", fileRefs) + " ";

        // Insert at the drop position
        if (insertIndex < 0) insertIndex = 0;
        if (insertIndex > MessageTextBox.Document.TextLength) insertIndex = MessageTextBox.Document.TextLength;

        MessageTextBox.Document.Insert(insertIndex, insertText);
        MessageTextBox.CaretOffset = insertIndex + insertText.Length;

        // Track dropped files for intent injection via spans
        int currentOffset = insertIndex;
        var filesExist = await Task.Run(() => validFiles.Select(f => File.Exists(f)).ToList());
        for (int i = 0; i < validFiles.Count; i++)
        {
            var file = validFiles[i];
            var mentionRef = fileRefs[i];
            try
            {
                if (filesExist[i])
                {
                    _mentionSpans?.Add(new MentionSpan(file, mentionRef.Substring(1))
                    {
                        StartOffset = currentOffset,
                        Length = mentionRef.Length
                    });
                }
                currentOffset += mentionRef.Length + 1; // +1 for the space separator
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApexCode] Failed to track dropped file {file}: {ex.Message}");
                _outputLogger?.Log($"Failed to track dropped file {file}: {ex.Message}");
                ShowError("Couldn't attach one or more dropped files.");
            }
        }

        MessageTextBox.Focus();
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsStreaming || _checkpointRestoreRunning) return;

        // FIX: Cancel retry countdown when user sends new message
        _retryCountdownTimer?.Stop();
        _retryCountdownTimer = null;

        // The plan review overlay covers the input, so a keystroke that still reaches the editor
        // (focus can linger there when the overlay opens) must not smuggle a turn out from behind it.
        if (PlanApprovalOverlay?.Visibility == Visibility.Visible)
        {
            // ReportPlanProblem("Decide on the plan, or close the review, before sending a new message.");
            return;
        }

        var text = MessageTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return;

        var explicitFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dependencyFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_mentionSpans != null)
        {
            var spanPaths = _mentionSpans.Where(s => s.Length > 0).Select(s => s.AbsolutePath).ToList();
            var existing = await Task.Run(() => spanPaths.Where(File.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase)).ConfigureAwait(true);
            foreach (var p in existing) explicitFiles.Add(p);
                
            if (_dependencyResolver != null && explicitFiles.Count > 0)
            {
                var resolved = await _dependencyResolver.ResolveDependenciesAsync(explicitFiles).ConfigureAwait(true);
                foreach (var p in resolved)
                {
                    if (IsContextInjectableFile(p) && !explicitFiles.Contains(p))
                        dependencyFiles.Add(p);
                }
            }
        }

        if (_chatService == null)
        {
            ShowError("Chat service is not available. Please configure a provider.");
            return;
        }

        if (!await ValidateApiKeyAsync()) return;

        var session = _chatService.ActiveSession;
        bool isFirstMessage = Messages.Count == 0;
        
        var selectedProviderId = "openai";
        if (ProviderCombo.SelectedItem is ComboBoxItem item && item.Tag != null)
            selectedProviderId = item.Tag.ToString();
            
        var selectedModelId = ModelPicker.SelectedModel?.ModelId ?? "gpt-4o";

        if (session == null)
        {
            var sessionName = text.Length > 40 ? text.Substring(0, 40).Trim() + "..." : text;
            session = await _chatService.CreateSessionAsync(sessionName, selectedProviderId, selectedModelId, "default-assistant");
            await _chatService.SetActiveSessionAsync(session.Id);
            _loadedSessionId = session.Id;
            WelcomeViewControl.Visibility = Visibility.Collapsed;
            MessagesList.Visibility = Visibility.Visible;
            await LoadSessionsAsync();
        }
        else 
        {
            if (isFirstMessage && session.Name.StartsWith("Chat "))
            {
                var sessionName = text.Length > 40 ? text.Substring(0, 40).Trim() + "..." : text;
                await _chatService.RenameSessionAsync(session.Id, sessionName);
            }
            if (session.ProviderId != selectedProviderId || session.ModelId != selectedModelId)
            {
                await _chatService.UpdateSessionModelAsync(session.Id, selectedProviderId, selectedModelId);
            }
        }

        MessageTextBox.Clear();
        ClearError();

        selectedProviderId = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (!string.IsNullOrEmpty(selectedProviderId) && _providerRepo != null)
        {
            try
            {
                var profile = await _providerRepo.GetByIdAsync(selectedProviderId);
                if (profile != null && profile.ProviderType?.ToLowerInvariant() != "ollama")
                {
                    var networkError = CheckNetworkOrShowError();
                    if (networkError != null)
                    {
                        ShowError(networkError);
                        IsStreaming = false;
                        StreamingProgressBar.Visibility = Visibility.Collapsed;
                        ThinkingAnimationPanel.Visibility = Visibility.Collapsed;
                        return;
                    }
                }
            }
            catch { }
        }

        var userMsg = new ChatMessageViewModel
        {
            Role = "user",
            Content = text,
            Timestamp = DateTime.Now
        };
        userMsg.AppendTextBlock(text);
        Messages.Add(userMsg);

        var providerName = (ProviderCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "unknown";
        var model = ModelPicker.SelectedModel?.ModelId ?? "unknown";
        _telemetryService?.TrackMessageSentAsync(providerName, model);

        // Rule 4: Wrap prompt in <user_request> on EVERY turn.
        // Wait, to keep DB history clean, we will just send the text directly. 
        // The wrapper will be injected ephemerally along with the <file_context>!
        var messageToSend = text;

        // The bubble is already in the list, so this turn only needs the shared streaming plumbing.
        await RunAgentTurnAsync(messageToSend, originalPrompt: text, addUserBubble: false, explicitFiles: explicitFiles.ToList(), dependencyFiles: dependencyFiles.ToList());

        // FIX: Clear the mention spans collection after a successful send (issue 12).
        // AvalonEdit zeros span lengths when the document is cleared, but the TextSegmentCollection
        // itself keeps growing. Stale entries with Length > 0 (possible after partial edits) could
        // inject ghost files into the next message silently.
        _mentionSpans?.Clear();

        MessageTextBox.Focus();
    }

    private async Task<(string Content, int CharsUsed)> ProcessFileForContext(string filePath, int maxBudgetChars, int index)
    {
        try
        {
            var content = await Task.Run(() => File.Exists(filePath) ? File.ReadAllText(filePath) : "").ConfigureAwait(true);
            var skeleton = AiAssistant.Engine.SkeletonEngine.ParserRouter.Parse(filePath, content, maxBudgetChars);
            
            var relativePath = filePath;
            var workspaceRoot = await GetActiveWorkspacePathAsync();
            if (!string.IsNullOrEmpty(workspaceRoot) && filePath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
            {
                relativePath = filePath.Substring(workspaceRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
            return ($"[{index}] {Path.GetFileName(filePath)} — OMITTED — error: {ex.Message}", 0);
        }
    }

    /// <summary>
    /// Single funnel for every agent turn this panel starts — chat input, plan revision request and
    /// plan approval all go through here.
    /// </summary>
    /// <remarks>
    /// The plan buttons used to call <c>IChatService.SendMessageAsync</c> directly, which meant the
    /// turn ran with no streaming indicator, no Stop button and no cancellation token: the overlay
    /// simply closed and the user was left with no sign that anything was happening. Owning that
    /// state in one place makes it impossible for a caller to skip it.
    /// </remarks>
    /// <param name="agentMessage">The message the agent receives and that is stored in history.</param>
    /// <param name="addUserBubble">
    /// False when the caller has already added the user's bubble to <see cref="Messages"/>.
    /// </param>
    private async Task RunAgentTurnAsync(string agentMessage, string? originalPrompt = null, bool addUserBubble = true, List<string>? explicitFiles = null, List<string>? dependencyFiles = null)
    {
        if (string.IsNullOrWhiteSpace(agentMessage) || _checkpointRestoreRunning) return;

        _isAwaitingAnswer = false;
        _userPressedStop = false;
        ComposerBorder.ClearValue(Border.BorderBrushProperty);
        ComposerBorder.ClearValue(Border.BorderThicknessProperty);
        UpdatePlaceholderVisibility();

        if (_chatService == null)
        {
            ShowError("Chat service is not available. Please configure a provider.");
            return;
        }

        var session = _chatService.ActiveSession;
        if (session == null)
        {
            ShowError("There is no active chat session.");
            return;
        }

        if (_isStreaming || _agentTurnRunning)
        {
            ShowError("The assistant is still responding. Wait for it to finish before sending again.");
            return;
        }

        _agentTurnRunning = true;
        // Guidance uses the same recovery context while keeping the user's bubble readable.
        if (_agentPause?.SessionId == session.Id && originalPrompt != "Continue")
        {
            originalPrompt ??= agentMessage;
            agentMessage = _agentPause.RecoveryPrompt + "\nUser guidance: " + agentMessage;
        }
        ShowAgentPause(null);
        ClearError();
        if (addUserBubble)
        {
            var content = originalPrompt ?? agentMessage;
            var userMsg = new ChatMessageViewModel
            {
                Role = "user",
                Content = content,
                Timestamp = DateTime.Now
            };
            userMsg.AppendTextBlock(content);
            Messages.Add(userMsg);
        }

        // Ensure we switch to the chat view, overriding any interleaved UpdateWelcomeViewAsync updates
        WelcomeViewControl.Visibility = Visibility.Collapsed;
        MessagesList.Visibility = Visibility.Visible;

        // FIX (Streaming Stop button): create a fresh CancellationTokenSource for this
        // generation. StopButton_Click cancels it; IChatService.SendMessageAsync already
        // threads the token through to the underlying IChatClient calls.
        _generationCts?.Dispose();
        _generationCts = new CancellationTokenSource();
        var generationToken = _generationCts.Token;

        try
        {
            IsStreaming = true;
            StreamingProgressBar.Visibility = Visibility.Visible;
            ThinkingAnimationPanel.Visibility = Visibility.Visible;
            ScrollToBottom();

            await _chatService.SendMessageAsync(session.Id, agentMessage, originalPrompt, generationToken, explicitFiles, dependencyFiles);
        }
        catch (OperationCanceledException)
        {
            // Defensive fallback; SendMessageAsync handles cancellation internally now.
            IsStreaming = false;
            StreamingProgressBar.Visibility = Visibility.Collapsed;
            ThinkingAnimationPanel.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            if (IsNetworkException(ex))
            {
                ShowError("Internet connection lost. Please check your network and try again.");
                ConnectionStatus = "🔴 No internet";
            }
            else
            {
                ShowError($"Failed to send message: {ex.Message}");
            }
            IsStreaming = false;
            StreamingProgressBar.Visibility = Visibility.Collapsed;
            ThinkingAnimationPanel.Visibility = Visibility.Collapsed;
            _telemetryService?.TrackErrorAsync("send_message", ex.Message);
        }
        finally
        {
            _generationCts?.Dispose();
            _generationCts = null;
            _agentTurnRunning = false;
            if (_agentPause != null) ShowAgentPause(_agentPause);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _userPressedStop = true;
        _telemetryService?.TrackEventAsync("generation_stopped", "user_clicked_stop");
        _generationCts?.Cancel();
    }

    private void OnToolExecuting(object? sender, AiAssistant.Core.Models.ToolCallInfo info)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var message = Messages.LastOrDefault(candidate => candidate.Role == "assistant");
            if (message == null || !_isStreaming) return;

            // AppendTool places the card chronologically right after any text that
            // has already streamed and registers it in the O(1) identity index.
            message.AppendTool(
                info.CallId,
                info.ToolName,
                info.Arguments);

            ScrollToBottom();
        });
    }

    private void OnCommandProgress(object? sender, CommandProgress progress)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_chatService?.ActiveSession?.Id != progress.Owner) return;
            foreach (var message in Messages)
                if (message._toolIndex.TryGetValue(progress.CallId, out var tool))
                {
                    tool.ResultText = progress.Result;
                    break;
                }
        });
    }

    private void OnToolCompleted(object? sender, AiAssistant.Core.Models.ToolResultInfo result)
    {
        // Must use Dispatcher because this event is raised from the background LLM streaming thread
        Dispatcher.InvokeAsync(() =>
        {
            var message = Messages.LastOrDefault(candidate => candidate.Role == "assistant");
            if (message == null) return;

            if (!string.IsNullOrEmpty(result.CallId) && message._toolIndex.TryGetValue(result.CallId, out var toolVm))
            {
                toolVm.Complete(result.ResultText, "Completed");
            }
        });
    }

    private void MessageTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (MentionPopup.IsOpen)
        {
            // Move selection without moving keyboard focus off MessageTextBox, so the
            // user can keep refining the fuzzy search while navigating the popup.
            if (e.Key == Key.Down)
            {
                e.Handled = true;
                if (MentionListBox.Items.Count > 0)
                    MentionListBox.SelectedIndex = Math.Min(MentionListBox.SelectedIndex + 1, MentionListBox.Items.Count - 1);
                if (MentionListBox.SelectedItem != null)
                    MentionListBox.ScrollIntoView(MentionListBox.SelectedItem);
                return;
            }
            if (e.Key == Key.Up)
            {
                e.Handled = true;
                if (MentionListBox.Items.Count > 0)
                    MentionListBox.SelectedIndex = Math.Max(MentionListBox.SelectedIndex - 1, 0);
                if (MentionListBox.SelectedItem != null)
                    MentionListBox.ScrollIntoView(MentionListBox.SelectedItem);
                return;
            }
            if (e.Key == Key.Enter || e.Key == Key.Tab)
            {
                e.Handled = true;
                _selectMentionTask = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        await SelectMentionAsync();
                    }
                    catch (Exception ex)
                    {
                        await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        System.Diagnostics.Debug.WriteLine($"[ApexCode] Mention selection failed: {ex}");
                        ShowError("Couldn't insert that file mention.");
                    }
                });
                return;
            }
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseMentionPopup();
                return;
            }
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            SendButton_Click(sender, new RoutedEventArgs());
        }
        // Shift+Enter produces a newline via AcceptsReturn="True"
    }

    private async void NewSessionBtn_Click(object sender, RoutedEventArgs e)
    {
        // Show loading overlay immediately on the UI thread so the panel never
        // appears frozen while the async DB session reset is in progress.
        NewChatLoadingOverlay.Visibility = Visibility.Visible;
        NewSessionBtn.IsEnabled = false;

        try
        {
            // Yield to the dispatcher at Background priority so WPF completes its
            // Render pass (priority 7) before we continue. Without this, both
            // "overlay=Visible" and "Messages.Clear()" land in the same frame, so
            // the overlay appears over an already-empty panel and is invisible
            // against the dark background. Background (priority 4) < Render (7),
            // so all layout/render work finishes before our continuation runs.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);

            // FIX: Cancel retry countdown when user starts new session
            _retryCountdownTimer?.Stop();
            _retryCountdownTimer = null;

            if (_taskExecutionViewModel != null && _taskExecutionViewModel.IsRunning)
            {
                var result = MessageBox.Show(
                    "You have an active plan running. Starting a new session will abort all remaining tasks. Are you sure you want to continue?",
                    "Active Tasks Running", 
                    MessageBoxButton.YesNo, 
                    MessageBoxImage.Warning);
                    
                if (result == MessageBoxResult.No)
                {
                    return;
                }
                
                await _taskExecutionViewModel.AbortPlanAsync();
                
                // Allow a brief moment for the abort message to be processed
                await System.Threading.Tasks.Task.Delay(500);
            }

            _generationCts?.Cancel();
            IsStreaming = false;
            StreamingProgressBar.Visibility = Visibility.Collapsed;
            ThinkingAnimationPanel.Visibility = Visibility.Collapsed;

            ClearError();
            ShowAgentPause(null);
            Messages.Clear();

            _isAwaitingAnswer = false;
            ComposerBorder.ClearValue(Border.BorderBrushProperty);
            ComposerBorder.ClearValue(Border.BorderThicknessProperty);
            UpdatePlaceholderVisibility();

            if (_taskExecutionViewModel != null)
            {
                _taskExecutionViewModel.Plan = null;
            }

            // Keep the user in their currently selected mode. The session change prevents
            // previous plans from leaking, so we don't need to force Plan mode.
            TokenUsageText.Text = string.Empty;
            ContextUsagePanel.Visibility = Visibility.Collapsed;
            TokenUsageProgressBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
            TokenUsageProgressBar.Value = 0;
            TokenUsageProgressBar.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "AccentPrimaryBrush");

            if (_chatService != null)
            {
                // Run the DB-backed session reset off the UI thread so the spinner
                // can animate freely without the main thread blocking on I/O.
                await System.Threading.Tasks.Task.Run(() => _chatService.SetActiveSessionAsync(string.Empty));
                _loadedSessionId = null;
                await UpdateWelcomeViewAsync();
                _telemetryService?.TrackEventAsync("new_chat_clicked", "toolbar");

                // FIX (ghost span cleanup): clear tracked @mention spans so stale entries
                // from the previous session can't silently inject files into the new one.
                _mentionSpans?.Clear();

                // Focus the input box for the new chat
                _ = Dispatcher.InvokeAsync(() => MessageTextBox.Focus(), DispatcherPriority.Input);
            }
            else
            {
                ShowError("Chat service is not available.");
            }
        }
        catch (Exception ex)
        {
            ShowError($"Failed to create new session: {ex.Message}");
        }
        finally
        {
            // Always restore the button and hide the overlay, even on error or early return.
            NewChatLoadingOverlay.Visibility = Visibility.Collapsed;
            NewSessionBtn.IsEnabled = true;
        }
    }

    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        _telemetryService?.TrackEventAsync("settings_opened");
        ShowSettingsPanel();
    }

    private void ShowSettingsPanel()
    {
        if (SettingsOverlay == null || SettingsViewControl == null) return;

        // Inject services into the settings view
        SettingsViewControl.SetServices(_providerRepo, _promptRepo, _personaService, _dbService, _modelCacheRepo, _chatClientFactory, _telemetryService, _settingsService, _outputLogger, _providerChangeNotifier, _modelFetchService);
        SettingsViewControl.ShowBackButton = true;
        // FIX: Guard against double-subscription on repeated opens (same pattern as ShowHistoryPanel).
        SettingsViewControl.ReturnToChat -= SettingsView_ReturnToChat;
        SettingsViewControl.ReturnToChat += SettingsView_ReturnToChat;
        SettingsViewControl.TriggerManualIndexing -= SettingsViewControl_TriggerManualIndexing;
        SettingsViewControl.TriggerManualIndexing += SettingsViewControl_TriggerManualIndexing;
        SettingsViewControl.StopManualIndexing -= SettingsViewControl_StopManualIndexing;
        SettingsViewControl.StopManualIndexing += SettingsViewControl_StopManualIndexing;
        SettingsViewControl.DownloadModelsRequested -= SettingsViewControl_DownloadModelsRequested;
        SettingsViewControl.DownloadModelsRequested += SettingsViewControl_DownloadModelsRequested;
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void SettingsViewControl_StopManualIndexing(object? sender, EventArgs e)
    {
        if (_indexingService != null)
        {
            Dispatcher.InvokeAsync(() => SettingsViewControl?.AppendIndexLog("Cancellation requested..."));
            _indexingService.StopFullScan();
        }
    }

    private void SettingsViewControl_TriggerManualIndexing(object? sender, EventArgs e)
    {
        if (_indexingService != null)
        {
            EventHandler<int>? startedHandler = null;
            EventHandler<(string FileName, int Remaining)>? fileHandler = null;
            EventHandler<string>? logHandler = null;
            EventHandler<(int ChunksIndexed, TimeSpan Duration)>? completedHandler = null;

            startedHandler = (s, count) => Dispatcher.InvokeAsync(() => SettingsViewControl?.AppendIndexLog($"Indexing started for {count} files..."));
            
            fileHandler = (s, args) => Dispatcher.InvokeAsync(() => SettingsViewControl?.AppendIndexLog($"Processed: {args.FileName} ({args.Remaining} remaining)"));

            logHandler = (s, msg) => Dispatcher.InvokeAsync(() => SettingsViewControl?.AppendIndexLog(msg));

            completedHandler = (s, args) =>
            {
                _indexingService.IndexingStarted -= startedHandler;
                _indexingService.FileIndexed -= fileHandler;
                _indexingService.IndexingLog -= logHandler;
                _indexingService.IndexingCompleted -= completedHandler;
                Dispatcher.InvokeAsync(() => 
                {
                    SettingsViewControl?.AppendIndexLog($"Indexing completed. {args.ChunksIndexed} chunks processed in {args.Duration.TotalSeconds:F1}s.");
                    SettingsViewControl?.StopIndexProgress();
                });
            };

            _indexingService.IndexingStarted += startedHandler;
            _indexingService.FileIndexed += fileHandler;
            _indexingService.IndexingLog += logHandler;
            _indexingService.IndexingCompleted += completedHandler;
            
            _ = Task.Run(async () =>
            {
                try
                {
                    var files = new List<string>();
                    if (_vsEnvService != null)
                    {
                        var workspacePath = await _vsEnvService.GetWorkspaceRootAsync();
                        if (!string.IsNullOrEmpty(workspacePath) && System.IO.Directory.Exists(workspacePath))
                        {
                            var extensions = new[] { ".cs", ".xaml", ".js", ".jsx", ".ts", ".tsx", ".py", ".c", ".cpp", ".cc", ".cxx", ".h", ".hpp", ".go", ".rs", ".java", ".rb", ".php", ".swift", ".kt", ".scala", ".sh", ".ps1", ".sql", ".json", ".xml", ".yaml", ".yml", ".toml", ".md", ".txt" };
                            files = System.IO.Directory.GetFiles(workspacePath, "*", System.IO.SearchOption.AllDirectories)
                                .Where(f => extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                                .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && !f.Contains("\\node_modules\\") && !f.Contains("\\.git\\") && !f.Contains("\\__pycache__\\"))
                                .ToList();
                        }
                    }

                    _indexingService.StartFullScan(files);
                }
                catch (Exception ex)
                {
                    _outputLogger?.Log($"Failed to start indexing: {ex.Message}");
                    Dispatcher.InvokeAsync(() => 
                    {
                        SettingsViewControl?.AppendIndexLog($"Error: {ex.Message}");
                        SettingsViewControl?.StopIndexProgress();
                    });
                }
            });
        }
        else
        {
            SettingsViewControl?.StopIndexProgress();
        }
    }

    private async void SettingsViewControl_DownloadModelsRequested(object? sender, EventArgs e)
    {
        if (_modelDownloader == null) return;
        
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var outputDirectory = System.IO.Path.Combine(appData, "AiAssistant", "EmbeddingModel");
            var progress = new Progress<double>(percent => 
            {
                Dispatcher.InvokeAsync(() => SettingsViewControl?.UpdateDownloadProgress(percent));
            });

            await _modelDownloader.DownloadModelsAsync(outputDirectory, progress);
            Dispatcher.InvokeAsync(() => SettingsViewControl?.OnDownloadComplete());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] Model download failed: {ex.Message}");
            Dispatcher.InvokeAsync(() => SettingsViewControl?.OnDownloadFailed(ex.Message));
        }
    }

    private void SettingsView_ReturnToChat(object? sender, EventArgs e)
    {
        if (SettingsViewControl != null)
        {
            SettingsViewControl.ReturnToChat -= SettingsView_ReturnToChat;
            SettingsViewControl.TriggerManualIndexing -= SettingsViewControl_TriggerManualIndexing;
            SettingsViewControl.StopManualIndexing -= SettingsViewControl_StopManualIndexing;
            SettingsViewControl.DownloadModelsRequested -= SettingsViewControl_DownloadModelsRequested;
            SettingsViewControl.ShowBackButton = false;
        }
        if (SettingsOverlay != null)
        {
            SettingsOverlay.Visibility = Visibility.Collapsed;
        }

        _ = LoadDatabaseConnectionsAsync();
    }

    private void HistoryBtn_Click(object sender, RoutedEventArgs e)
    {
        _telemetryService?.TrackEventAsync("history_opened");
        ShowHistoryPanel();
    }

    private void ViewPlanBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_taskExecutionViewModel?.Plan != null)
        {
            _chatService.RaiseScrollToPlanEvent(_taskExecutionViewModel.Plan.Id);
        }
    }

    private void ShowHistoryPanel()
    {
        if (HistoryOverlay == null || HistoryPanelControl == null) return;

        // FIX: Unsubscribe before subscribing so that repeated opens don't accumulate duplicate
        // handlers. Without this, every open adds another += entry; selecting a session then
        // fires LoadActiveSessionMessagesAsync N times (once per open since last VS restart).
        HistoryPanelControl.ReturnToChat -= HistoryPanel_ReturnToChat;
        HistoryPanelControl.ReturnToChat += HistoryPanel_ReturnToChat;
        HistoryPanelControl.NewChatRequested -= HistoryPanel_NewChatRequested;
        HistoryPanelControl.NewChatRequested += HistoryPanel_NewChatRequested;
        HistoryOverlay.Visibility = Visibility.Visible;
        _ = HistoryPanelControl.RefreshHistoryAsync();
    }

    private bool _isLoadingHistoryConversation;

    private async void HistoryPanel_ReturnToChat(object? sender, string? sessionId)
    {
        if (_isLoadingHistoryConversation) return;
        if (HistoryPanelControl != null)
        {
            HistoryPanelControl.ReturnToChat -= HistoryPanel_ReturnToChat;
            HistoryPanelControl.NewChatRequested -= HistoryPanel_NewChatRequested;
        }
        if (HistoryOverlay != null)
        {
            HistoryOverlay.Visibility = Visibility.Collapsed;
        }

        // Keep the overlay visible for activation, data loading, and chat rendering.
        if (sessionId != null && _chatService != null)
        {
            _isLoadingHistoryConversation = true;
            ConversationLoadingOverlay.Visibility = Visibility.Visible;
            try
            {
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                await Task.Run(() => _chatService.SetActiveSessionAsync(sessionId));
                await LoadActiveSessionMessagesAsync(true);
                await LoadSessionsAsync();
                _ = _telemetryService?.TrackEventAsync("history_session_loaded");
                // Allow pending bindings/layout to finish before uncovering the chat.
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
            catch (Exception ex)
            {
                ShowError($"Failed to load conversation: {ex.Message}");
            }
            finally
            {
                ConversationLoadingOverlay.Visibility = Visibility.Collapsed;
                _isLoadingHistoryConversation = false;
            }
        }
    }

    private void HistoryPanel_NewChatRequested(object? sender, EventArgs e)
    {
        // FIX (History empty state quick action): close the history overlay and start
        // a new session directly, instead of requiring Back then New Chat.
        if (HistoryPanelControl != null)
        {
            HistoryPanelControl.ReturnToChat -= HistoryPanel_ReturnToChat;
            HistoryPanelControl.NewChatRequested -= HistoryPanel_NewChatRequested;
        }
        if (HistoryOverlay != null)
        {
            HistoryOverlay.Visibility = Visibility.Collapsed;
        }

        NewSessionBtn_Click(this, new RoutedEventArgs());
    }

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModeCombo.SelectedItem is ComboBoxItem selectedItem && _chatService != null)
        {
            var mode = selectedItem.Tag as string;
            if (!string.IsNullOrEmpty(mode))
            {
                _chatService.ActiveMode = mode;
                
                // Update mode hint
                if (ModeHintText != null)
                {
                    ModeHintText.Text = AiAssistant.Core.Services.AgentModes.IsAct(mode) 
                        ? "Write access: can execute commands and modify files" 
                        : "Read-only: proposes a plan for your review";
                }
            }
        }
    }

    private async void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            // When provider changes, update available models
            var providerId = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (string.IsNullOrEmpty(providerId) || _providerRepo == null || _modelFetchService == null || ModelPicker == null) return;

            // Prevent re-fetching if the provider hasn't changed (e.g. during tab switch)
            if (_lastFetchedProviderId == providerId && ModelPicker.HasModels())
            {
                return;
            }

            var profile = await _providerRepo.GetByIdAsync(providerId);
            if (profile == null) return;

            // Check internet connectivity before attempting to fetch models (skip for local providers)
            if (profile.ProviderType?.ToLowerInvariant() != "ollama")
            {
                var networkError = CheckNetworkOrShowError();
                if (networkError != null)
                {
                    ShowError(networkError);
                    ModelPicker.SetModels(new List<ModelInfo>());
                    ConnectionStatus = "ðŸ”´ No internet";
                    return;
                }
            }

            try
            {
                ModelPicker.ShowLoading();
                var models = await _modelFetchService.FetchModelsAsync(profile.ProviderType, profile.ApiKey ?? "", profile.ApiEndpoint, profile.ModelFetchEndpoint);
                if (models.Count == 0)
                {
                    ModelPicker.ShowError("No models found or authentication failed.");
                }
                else
                {
                    ModelPicker.SetModels(models);
                    _lastFetchedProviderId = providerId;
                    if (!string.IsNullOrEmpty(profile.DefaultModel))
                    {
                        var match = models.FirstOrDefault(m => m.ModelId == profile.DefaultModel);
                        if (match != null)
                            ModelPicker.SelectedModel = match;
                    }
                }
            }
            catch (Exception ex)
            {
                // If the exception looks like a network error, show a clear message
                if (IsNetworkException(ex))
                {
                    ShowError("Internet connection lost. Please check your network and try again.");
                    ModelPicker.SetModels(new List<ModelInfo>());
                }
                else
                {
                    ModelPicker.ShowError($"Failed to fetch models: {ex.Message}");
                }
            }

            // Update connection status when provider changes
            UpdateConnectionStatusDirect(profile);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] ProviderCombo_SelectionChanged error: {ex.Message}");
        }
    }

    private void UpdateConnectionStatusDirect(AiAssistant.Storage.Models.ProviderProfile profile)
    {
        if (profile.ProviderType?.ToLowerInvariant() == "ollama")
        {
            ConnectionStatus = $"🟡 Local ({profile.Name})";
        }
        else if (!string.IsNullOrEmpty(profile.ApiKey))
        {
            ConnectionStatus = $"🟢 Connected ({profile.Name})";
        }
        else
        {
            ConnectionStatus = $"🔴 No API key ({profile.Name})";
        }
    }

    /// <summary>
    /// Determines if an exception is network-related (connection refused, timeout, DNS failure, etc.)
    /// </summary>
    private static bool IsNetworkException(Exception ex)
    {
        // Check common network exception types
        if (ex is System.Net.Http.HttpRequestException ||
            ex is System.Net.Sockets.SocketException ||
            ex is System.Net.WebException ||
            ex is TimeoutException ||
            ex is TaskCanceledException)
            return true;

        // Check if the message contains network-related keywords
        var message = ex.Message.ToLowerInvariant();
        if (message.Contains("connection") ||
            message.Contains("timeout") ||
            message.Contains("refused") ||
            message.Contains("unreachable") ||
            message.Contains("network") ||
            message.Contains("name resolution") ||
            message.Contains("could not connect") ||
            message.Contains("unable to connect") ||
            message.Contains("no such host"))
            return true;

        // Check inner exceptions
        if (ex.InnerException != null)
            return IsNetworkException(ex.InnerException);

        return false;
    }

    /// <summary>
    /// Checks if an error message string indicates a network connectivity issue.
    /// </summary>
    private static bool IsNetworkErrorMessage(string error)
    {
        if (string.IsNullOrEmpty(error))
            return false;

        var lower = error.ToLowerInvariant();
        
        // Ensure application-level timeouts aren't treated as network disconnects
        if (lower.Contains("turn timeout"))
            return false;

        return lower.Contains("connection") ||
               lower.Contains("timeout") ||
               lower.Contains("refused") ||
               lower.Contains("unreachable") ||
               lower.Contains("network") ||
               lower.Contains("name resolution") ||
               lower.Contains("could not connect") ||
               lower.Contains("unable to connect") ||
               lower.Contains("no such host") ||
               lower.Contains("internet") ||
               lower.Contains("offline");
    }

    private void ModelPicker_RetryClicked(object sender, EventArgs e)
    {
        _lastFetchedProviderId = null;
        ProviderCombo_SelectionChanged(this, null!);
    }

    private async void ModelPicker_SelectedModelChanged(object sender, EventArgs e)
    {
        try
        {
            if (ModelPicker.SelectedModel != null && _providerRepo != null)
            {
                var providerId = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                if (!string.IsNullOrEmpty(providerId))
                {
                    var profile = await _providerRepo.GetByIdAsync(providerId);
                    if (profile != null)
                    {
                        bool needsUpdate = false;
                        var updatedProfile = profile;

                        if (profile.DefaultModel != ModelPicker.SelectedModel.ModelId)
                        {
                            updatedProfile = updatedProfile with { DefaultModel = ModelPicker.SelectedModel.ModelId };
                            needsUpdate = true;
                        }

                        if (ModelPicker.SelectedModel.ContextWindowTokens > 0 && profile.ContextWindowTokens != ModelPicker.SelectedModel.ContextWindowTokens)
                        {
                            updatedProfile = updatedProfile with { ContextWindowTokens = ModelPicker.SelectedModel.ContextWindowTokens };
                            needsUpdate = true;
                        }

                        if (needsUpdate)
                        {
                            await _providerRepo.UpdateAsync(updatedProfile);
                            
                            // Force refresh token usage UI
                            if (_chatService?.ActiveSession != null)
                            {
                                _ = _chatService.GetSessionMessagesAsync(_chatService.ActiveSession.Id);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] ModelPicker_SelectedModelChanged error: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks whether the system has an active internet connection by pinging a well-known endpoint.
    /// Returns false quickly if no network adapter is up.
    /// </summary>
    private static bool HasInternetConnection()
    {
        try
        {
            // Just check if any network interface is up, don't use ping as it's unreliable in some networks
            return NetworkInterface.GetIsNetworkAvailable();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks internet connectivity and returns a user-friendly error message if disconnected.
    /// Returns null if connected (no error).
    /// </summary>
    private static string? CheckNetworkOrShowError()
    {
        if (!NetworkInterface.GetIsNetworkAvailable())
            return "Internet connection lost. Please check your network settings and try again.";

        return null;
    }

    /// <summary>
    /// Updates the connection status indicator based on whether a valid API key is configured.
    /// </summary>
    private void UpdateConnectionStatus()
    {
        var selectedProviderId = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (string.IsNullOrEmpty(selectedProviderId))
        {
            ConnectionStatus = "⚪ Not configured";
            return;
        }

        if (_providerRepo == null)
        {
            ConnectionStatus = "⚪ Not configured";
            return;
        }

        try
        {
            var profiles = _providerRepo.GetAllAsync().GetAwaiter().GetResult();
            UpdateConnectionStatusInternal(profiles);
        }
        catch
        {
            ConnectionStatus = "⚪ Unknown";
        }
    }

    private void UpdateConnectionStatusInternal(IEnumerable<AiAssistant.Storage.Models.ProviderProfile> profiles)
    {
        var selectedProviderId = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (string.IsNullOrEmpty(selectedProviderId))
        {
            ConnectionStatus = "⚪ Not configured";
            return;
        }

        var profile = profiles.FirstOrDefault(p => p.Id == selectedProviderId);

        if (profile != null)
        {
            if (profile.ProviderType?.ToLowerInvariant() == "ollama")
            {
                ConnectionStatus = $"🟡 Local ({profile.Name})";
            }
            else if (!string.IsNullOrEmpty(profile.ApiKey))
            {
                ConnectionStatus = $"🟢 Connected ({profile.Name})";
            }
            else
            {
                ConnectionStatus = $"🔴 No API key ({profile.Name})";
            }
        }
        else
        {
            ConnectionStatus = "⚪ Unknown Provider";
        }
    }

    private void MessagesList_Loaded(object sender, RoutedEventArgs e)
    {
        _messagesScrollViewer = MessagesList.Template.FindName("PART_ScrollViewer", MessagesList) as ScrollViewer;
    }

    private bool _userScrolledUp = false;

    private void MessagesScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var scrollViewer = (ScrollViewer)sender;

        // If the user has reached the bottom (within a 25px buffer), reset the flag so auto-scroll can resume.
        if (scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 25)
        {
            _userScrolledUp = false;
        }
        // If the extent didn't grow and the scroll moved UP, it means the user manually scrolled up.
        // We ignore e.VerticalChange > 0 (scrolling down or animating) unless it hits the bottom.
        else if (e.ExtentHeightChange == 0 && e.VerticalChange < 0)
        {
            _userScrolledUp = true;
        }
    }

    private void ScrollToBottom()
    {
        if (Interlocked.Exchange(ref _scrollScheduled, 1) != 0) return;

        _ = Dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _scrollScheduled, 0);
            var scrollViewer = _messagesScrollViewer ??=
                MessagesList.Template.FindName("PART_ScrollViewer", MessagesList) as ScrollViewer;
            if (scrollViewer == null) return;

            // Ensure our ScrollChanged event is hooked up
            scrollViewer.ScrollChanged -= MessagesScrollViewer_ScrollChanged;
            scrollViewer.ScrollChanged += MessagesScrollViewer_ScrollChanged;

            // Smart Scroll: Auto-scroll unless the user has intentionally scrolled up to read history
            if (!_userScrolledUp)
            {
                ScrollAnimationHelper.AnimateScroll(scrollViewer, scrollViewer.ScrollableHeight);
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Shows an error message in the chat panel's error banner.
    /// </summary>
    public void ShowErrorMessage(string message) => ShowError(message);

    internal void ShowError(string message)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!string.IsNullOrEmpty(message)) AgentPauseBanner.Visibility = Visibility.Collapsed;
            ErrorMessage = message;
            ErrorBanner.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        });
    }

    private void ClearError()
    {
        Dispatcher.InvokeAsync(() =>
        {
            ErrorMessage = "";
            ErrorBanner.Visibility = Visibility.Collapsed;
        });
    }

    private void ErrorCloseBtn_Click(object sender, RoutedEventArgs e)
    {
        ClearError();
    }

    private void OnApprovalRequested(object? sender, ApprovalRequest req)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var activeMessage = Messages.LastOrDefault(message => message.Role == "assistant");
            if (activeMessage == null) return;

            // ToolExecuting is raised first for normal model calls. Reuse that row so a
            // mutation transitions from "Running" to "Awaiting approval" instead of
            // appearing twice. The fallback also supports callers outside the chat loop.
            var toolVm = activeMessage.FindInflightTool(req.ToolName);

            if (toolVm == null)
            {
                var parameters = SerializeToolParameters(req.Parameters);
                toolVm = activeMessage.AppendTool(req.Id, req.ToolName, null);
                toolVm.Parameters = parameters;
                toolVm.StatusText = "Running…";
            }

            toolVm.Id = req.Id;

            toolVm.ApproveCmd = new Helpers.RelayCommand(async _ =>
            {
                toolVm.NeedsApproval = false;
                toolVm.StatusText = "Executing…";
                if (_approvalService != null) await _approvalService.ApproveAsync(req.Id);
            });

            toolVm.RejectCmd = new Helpers.RelayCommand(async _ =>
            {
                toolVm.NeedsApproval = false;
                toolVm.StatusText = "Rejected";
                if (_approvalService != null) await _approvalService.RejectAsync(req.Id);
            });

            toolVm.AlwaysAllowCmd = new Helpers.RelayCommand(async _ =>
            {
                toolVm.NeedsApproval = false;
                toolVm.StatusText = "Executing…";
                if (_settingsService != null) await _settingsService.SetToolApprovalModeAsync(req.ToolName, "allowall");
                if (_approvalService != null) await _approvalService.ApproveAsync(req.Id);
            });

            toolVm.NeedsApproval = true;
            toolVm.StatusText = "Awaiting approval";
            toolVm.IsExpanded = toolVm.NeedsApproval || toolVm.HasChangePreview;

            ScrollToBottom();
        });
    }

    private static string SerializeToolParameters(object? parameters)
    {
        if (parameters == null) return string.Empty;
        if (parameters is string text) return text;
        return System.Text.Json.JsonSerializer.Serialize(
            parameters,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private void LogBus_EntryPublished(object? sender, AiAssistant.Core.Services.LogEntry entry)
    {
        if (entry.Category != AiAssistant.Core.Services.LogCategory.Tool) return;

        var isStart = entry.Message.StartsWith("► ", StringComparison.Ordinal)
                   || entry.Message.StartsWith("\u25ba ", StringComparison.Ordinal);
        
        var isCompletion = entry.Message.StartsWith("[TOOL_DONE] ", StringComparison.Ordinal)
                        || entry.Message.StartsWith("◄ ", StringComparison.Ordinal)
                        || entry.Message.StartsWith("\u25c4 ", StringComparison.Ordinal);
                        
        if (!isStart && !isCompletion) return;

        Dispatcher.InvokeAsync(() =>
        {
            var activeMessage = Messages.LastOrDefault(message => message.Role == "assistant");
            if (activeMessage == null) return;

            if (isStart)
            {
                var toolName = ExtractToolName(entry.Message, '(');
                if (string.IsNullOrWhiteSpace(toolName)) return;

                // ToolExecuting normally creates this row first via AppendTool.
                // The log-bus path is a deliberate fallback for a reloaded tool window
                // or any tool that logs directly without raising the ChatService event.
                // Only add a new card if there is no in-flight match for this tool name.
                var existing = activeMessage.FindInflightTool(toolName);
                if (existing == null)
                {
                    var parameters = !string.IsNullOrWhiteSpace(entry.Detail)
                        ? entry.Detail
                        : ExtractToolParameters(entry.Message);
                    var vm = activeMessage.AppendTool(
                        Guid.NewGuid().ToString(),
                        toolName,
                        null);
                    vm.Parameters = parameters;
                    vm.OccurredAt = entry.Timestamp;
                }
                ScrollToBottom();
                return;
            }

            string completedToolName;
            string result;

            if (entry.Message.StartsWith("[TOOL_DONE] ", StringComparison.Ordinal))
            {
                const string toolDonePrefix = "[TOOL_DONE] ";
                var rest = entry.Message.Substring(toolDonePrefix.Length);
                var colonIndex = rest.IndexOf(": ", StringComparison.Ordinal);
                completedToolName = colonIndex > 0 ? rest.Substring(0, colonIndex).Trim() : rest.Trim();
                result = !string.IsNullOrWhiteSpace(entry.Detail)
                    ? entry.Detail
                    : (colonIndex > 0 ? rest.Substring(colonIndex + 2).Trim() : string.Empty);
            }
            else
            {
                // Format is "◄ toolName → result" or "\u25c4 toolName \u2192 result"
                var arrowIndex = entry.Message.IndexOf(" → ", StringComparison.Ordinal);
                if (arrowIndex < 0) arrowIndex = entry.Message.IndexOf(" \u2192 ", StringComparison.Ordinal);
                
                if (arrowIndex > 2)
                {
                    completedToolName = entry.Message.Substring(2, arrowIndex - 2).Trim();
                    result = string.IsNullOrWhiteSpace(entry.Detail)
                        ? entry.Message.Substring(arrowIndex + 3).Trim()
                        : entry.Detail;
                }
                else return; // Could not parse
            }

            // O(1) path: find the last in-flight card for this tool name.
            var toolCall = activeMessage.FindInflightTool(completedToolName);

            // A completion is still useful if the panel subscribed after the start.
            // Only synthesize it for a currently-streaming response so background tool
            // logs cannot be appended to an unrelated, already-finished chat turn.
            if (toolCall == null)
            {
                if (!_isStreaming) return;
                toolCall = activeMessage.AppendTool(
                    Guid.NewGuid().ToString(),
                    completedToolName,
                    null);
                toolCall.OccurredAt = entry.Timestamp;
            }

            var completionStatus = result.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   result.IndexOf("rejected", StringComparison.OrdinalIgnoreCase) >= 0
                ? "Failed"
                : "Completed";
            toolCall.Complete(result, completionStatus);
            toolCall.IsExpanded = toolCall.HasChangePreview;
            ScrollToBottom();
        });
    }

    private static string? ExtractToolName(string message, char delimiter)
    {
        var delimiterIndex = message.IndexOf(delimiter);
        if (delimiterIndex <= 2) return null;
        return message.Substring(2, delimiterIndex - 2).Trim();
    }

    private static string ExtractToolParameters(string message)
    {
        var openingParenthesis = message.IndexOf('(');
        var closingParenthesis = message.LastIndexOf(')');
        if (openingParenthesis < 0 || closingParenthesis <= openingParenthesis)
        {
            return string.Empty;
        }

        return message.Substring(openingParenthesis + 1, closingParenthesis - openingParenthesis - 1).Trim();
    }

    private void UIElement_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!e.Handled)
        {
            e.Handled = true;
            var scrollViewer = _messagesScrollViewer ??= 
                MessagesList.Template.FindName("PART_ScrollViewer", MessagesList) as ScrollViewer;

            if (scrollViewer != null)
            {
                // Convert mouse wheel delta to a pixel offset.
                // Standard delta is 120. SystemParameters.WheelScrollLines is typically 3. We use ~16px per line.
                double scrollDistance = (e.Delta / 120.0) * SystemParameters.WheelScrollLines * 16.0;
                scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - scrollDistance);
            }
        }
    }
    #region Checkpoints

    private void OnCheckpointCreated(object? sender, CheckpointInfo cp)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var msg = Messages.FirstOrDefault(m => m.Id == cp.MessageId);
            if (msg != null)
            {
                msg.CheckpointId = cp.Id;
                msg.HasCheckpoint = true;
            }
        });
    }

    private void CheckpointBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private async void CheckpointCompare_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is ChatMessageViewModel msg && msg.HasCheckpoint && _checkpointService != null && _vsEnvService != null)
        {
            try
            {
                var sessionId = _chatService?.ActiveSession?.Id;
                if (string.IsNullOrEmpty(sessionId))
                {
                    ShowError("No active session.");
                    return;
                }

                var checkpoints = await _checkpointService.GetCheckpointsForSessionAsync(sessionId);
                var checkpoint = checkpoints.FirstOrDefault(c => c.Id == msg.CheckpointId);
                if (checkpoint == null)
                {
                    ShowError("Checkpoint not found.");
                    return;
                }

                // Diff from the checkpoint to the current state.
                // Assuming we can leave toCommitHash null for working tree diff, OR get diff for the commit vs parent.
                // For "compare", we usually want to see what changed *in this checkpoint*.
                // But the user might want to see the difference between the checkpoint and current workspace.
                var diffs = await _checkpointService.GetDiffAsync(checkpoint.CommitHash);
                
                if (diffs == null || diffs.Count == 0)
                {
                    MessageBox.Show("No differences found between the checkpoint and the current workspace.", "Checkpoint Compare", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var diffWindow = new AiAssistant.UI.Windows.CheckpointDiffWindow(diffs, _checkpointService, _vsEnvService, checkpoint.Id, checkpoint.CommitHash);
                diffWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                ShowError($"Failed to compare checkpoint: {ex.Message}");
            }
        }
    }

    private async void CheckpointRestoreTaskWorkspace_Click(object sender, RoutedEventArgs e)
    {
        await RestoreCheckpointAsync(sender, "taskAndWorkspace");
    }

    private async void CheckpointRestoreTask_Click(object sender, RoutedEventArgs e)
    {
        await RestoreCheckpointAsync(sender, "task");
    }

    private async void CheckpointRestoreWorkspace_Click(object sender, RoutedEventArgs e)
    {
        await RestoreCheckpointAsync(sender, "workspace");
    }

    private async Task RestoreCheckpointAsync(object sender, string restoreType)
    {
        if (_agentTurnRunning || IsStreaming || _checkpointRestoreRunning)
        {
            ShowError("Wait for the current action to finish before restoring a checkpoint.");
            return;
        }
        if (sender is MenuItem item && item.Tag is ChatMessageViewModel msg && msg.HasCheckpoint && !string.IsNullOrEmpty(msg.CheckpointId) && _chatService != null)
        {
            if (!AiAssistant.UI.Windows.CheckpointRestoreDialog.Confirm(Window.GetWindow(this), restoreType)) return;
            _checkpointRestoreRunning = true;
            var sessionId = _chatService.ActiveSession?.Id;
            try
            {
                var restoreResult = await _chatService.RestoreCheckpointAsync(msg.CheckpointId, restoreType);
                if (restoreResult.Success)
                {
                    if ((restoreType == "task" || restoreType == "taskAndWorkspace") && _chatService.ActiveSession?.Id == sessionId)
                    {
                        await LoadActiveSessionMessagesAsync(forceReload: true);
                        // Preserve any existing draft as well as the reverted request.
                        MessageTextBox.Text = string.IsNullOrWhiteSpace(MessageTextBox.Text)
                            ? msg.Content : msg.Content + Environment.NewLine + Environment.NewLine + MessageTextBox.Text;
                        MessageTextBox.Focus();
                    }
                }
                else
                {
                    ShowError($"Failed to restore checkpoint: {restoreResult.ErrorMessage}");
                }
            }
            catch (Exception ex) { ShowError($"Failed to restore checkpoint: {ex.Message}"); }
            finally { _checkpointRestoreRunning = false; }
        }
    }

    private bool _checkpointRestoreRunning;

    private void UserEditBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ChatMessageViewModel msg)
        {
            var parent = VisualTreeHelper.GetParent(btn);
            while (parent != null && !(parent is Grid g && g.FindName("UserEditArea") != null))
            {
                parent = VisualTreeHelper.GetParent(parent);
            }
            if (parent is Grid grid)
            {
                if (grid.FindName("UserEditArea") is Border editArea)
                {
                    editArea.Visibility = Visibility.Visible;
                }
            }
        }
    }

    private void UserEditCancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            var parent = VisualTreeHelper.GetParent(btn);
            while (parent != null && !(parent is Border b && b.Name == "UserEditArea"))
            {
                parent = VisualTreeHelper.GetParent(parent);
            }
            if (parent is Border editArea)
            {
                editArea.Visibility = Visibility.Collapsed;
            }
        }
    }

    private async void UserEditRestoreChat_Click(object sender, RoutedEventArgs e)
    {
        await PerformUserEditRestore(sender, false);
    }

    private async void UserEditRestoreAll_Click(object sender, RoutedEventArgs e)
    {
        await PerformUserEditRestore(sender, true);
    }

    private async Task PerformUserEditRestore(object sender, bool restoreWorkspace)
    {
        if (sender is Button btn && btn.Tag is ChatMessageViewModel msg)
        {
            string editedText = msg.Content;
            
            var parent = VisualTreeHelper.GetParent(btn);
            while (parent != null && !(parent is Border b && b.Name == "UserEditArea"))
            {
                parent = VisualTreeHelper.GetParent(parent);
            }
            if (parent is Border editArea && editArea.Child is Grid grid)
            {
                if (grid.Children[0] is TextBox tb) editedText = tb.Text;
                editArea.Visibility = Visibility.Collapsed;
            }

            var idx = Messages.IndexOf(msg);
            if (idx >= 0)
            {
                var toRemove = Messages.Skip(idx).ToList();
                foreach (var r in toRemove) Messages.Remove(r);

                MessageTextBox.Text = editedText;
                SendButton_Click(this, new RoutedEventArgs());
            }
        }
    }

    private void DismissCheckpointError_Click(object sender, RoutedEventArgs e)
    {
        if (FindName("CheckpointErrorBanner") is Border banner)
        {
            banner.Visibility = Visibility.Collapsed;
        }
    }

    public void SetCheckpointError(string errorMessage)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (FindName("CheckpointErrorBanner") is Border banner && FindName("CheckpointErrorText") is TextBlock textBlock)
            {
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    textBlock.Text = errorMessage;
                    banner.Visibility = Visibility.Visible;
                }
                else
                {
                    banner.Visibility = Visibility.Collapsed;
                }
            }
        });
    }

    /// <summary>
    /// Returns true if a file should be inlined into the LLM context window.
    /// The allowed extensions come from <see cref="ISettingsService.WorkspaceExtensions"/>
    /// so the user can control them from Settings → PEKKA → Workspace Extensions.
    /// Binary output dirs (bin/, obj/, packages/) are always excluded regardless of extension.
    /// </summary>
    private bool IsContextInjectableFile(string filePath)
    {
        // 1. Always skip output and package directories — they contain compiled artefacts,
        //    never source code the LLM needs to read inline.
        var normalized = filePath.Replace('/', '\\');
        if (normalized.IndexOf("\\packages\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalized.IndexOf("\\bin\\",      StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalized.IndexOf("\\obj\\",      StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalized.IndexOf("\\.vs\\",      StringComparison.OrdinalIgnoreCase) >= 0)
            return false;

        // 2. Extension must be in the user-configurable WorkspaceExtensions list.
        //    Falls back to a minimal safe set if the service is unavailable.
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext)) return false;

        var allowedRaw = _settingsService?.WorkspaceExtensions
            ?? ".cs,.ts,.js,.py,.java,.xaml,.xml,.json,.yaml,.yml,.md,.sql,.html,.css,.razor";

        var allowed = allowedRaw
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return allowed.Contains(ext);
    }

    #endregion

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string name = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class ScrollAnimationHelper
{
    public static readonly DependencyProperty VerticalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "VerticalOffset", typeof(double), typeof(ScrollAnimationHelper),
            new UIPropertyMetadata(0.0, OnVerticalOffsetChanged));

    public static void SetVerticalOffset(FrameworkElement target, double value) => target.SetValue(VerticalOffsetProperty, value);
    public static double GetVerticalOffset(FrameworkElement target) => (double)target.GetValue(VerticalOffsetProperty);

    private static void OnVerticalOffsetChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is ScrollViewer scrollViewer)
            scrollViewer.ScrollToVerticalOffset((double)e.NewValue);
    }

    public static void AnimateScroll(ScrollViewer scrollViewer, double toValue)
    {
        var animation = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = scrollViewer.VerticalOffset,
            To = toValue,
            Duration = new Duration(TimeSpan.FromMilliseconds(250)),
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase 
            { 
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn 
            }
        };
        scrollViewer.BeginAnimation(VerticalOffsetProperty, animation);
    }
}

/// <summary>
/// Static helper to access package info from non-VSIX projects.
/// </summary>
public static class AiAssistantPackage
{
    // FIX: Correct stub GUID to match ApexCodePackage.PackageGuidString.
    // The previous placeholder ("c1a2b3c4-...") caused version.txt and theme.txt to be written
    // to/read from a wrong LocalAppData subdirectory, so the update banner fired every launch
    // and theme preferences were never persisted.
    public const string PackageGuidString = "26737c4c-cf02-418d-9185-4bbc9518b5a9";
    public const string Id = "26737c4c-cf02-418d-9185-4bbc9518b5a9";
    public class MentionColorizer : ICSharpCode.AvalonEdit.Rendering.DocumentColorizingTransformer
    {
        protected override void ColorizeLine(ICSharpCode.AvalonEdit.Document.DocumentLine line)
        {
            var text = CurrentContext.Document.GetText(line);
            int start = 0;
            while ((start = text.IndexOf('@', start)) >= 0)
            {
                int end = start + 1;
                while (end < text.Length && !char.IsWhiteSpace(text[end]))
                    end++;
                
                base.ChangeLinePart(line.Offset + start, line.Offset + end, element => 
                {
                    element.TextRunProperties.SetForegroundBrush(System.Windows.Media.Brushes.DodgerBlue);
                });
                start = end;
            }
        }
    }
}





