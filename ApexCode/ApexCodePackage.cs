using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using AiAssistant.Core.Services;
using AiAssistant.Engine.Services;
using AiAssistant.Llm.Services;
using AiAssistant.Storage.Database;
using AiAssistant.Storage.Models;
using AiAssistant.Storage.Repositories;
using AiAssistant.Tools.Functions;
using AiAssistant.Tools.Services;
using AiAssistant.UI.Controls;
using ApexCode.Commands;
using ApexCode.ToolWindows;
using ApexCode.Services;
using ApexCode.Tools;
using ApexCode.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System.Runtime.InteropServices;
using Task = System.Threading.Tasks.Task;

// ---------------------------------------------------------------------------------------------
// Assembly binding redirects for the extension's private dependencies are declared in
// bindingRedirects.pkgdef (included directly as a VSIXSourceItem in the project).
// They cannot be declared here as [assembly: ProvideBindingRedirection(...)] attributes
// because the VSSDK CreatePkgDef tool has a known bug: its version string parser cannot
// handle version components with more than one digit (e.g. 10.0.0.10, 10.8.0.0 fail).
// The manual .pkgdef file bypasses CreatePkgDef entirely and is merged at install time.
// ---------------------------------------------------------------------------------------------

namespace ApexCode;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuidString)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideToolWindow(typeof(ChatToolWindow), Style = VsDockStyle.Tabbed, Window = Microsoft.VisualStudio.Shell.Interop.ToolWindowGuids.SolutionExplorer, Width = 450, Height = 800)]
[ProvideToolWindow(typeof(SettingsToolWindow))]
[ProvideToolWindow(typeof(LogToolWindow))]
[ProvideOptionPage(typeof(Options.GeneralOptionsPage), "ApexCode", "General", 0, 0, true)]
[ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideBindingPath]
public sealed class ApexCodePackage : AsyncPackage
{
    public const string PackageGuidString = "26737c4c-cf02-418d-9185-4bbc9518b5a9";

    public static IServiceProvider SystemServiceProvider { get; private set; }

    private static readonly TaskCompletionSource<IServiceProvider> _serviceProviderTcs = new TaskCompletionSource<IServiceProvider>();
    private Microsoft.Extensions.DependencyInjection.ServiceProvider _serviceProvider;

    private static bool _assemblyResolveRegistered;

    static ApexCodePackage()
    {
        if (!_assemblyResolveRegistered)
        {
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
            _assemblyResolveRegistered = true;
        }
    }

    public ApexCodePackage()
    {
    }

    private static System.Reflection.Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
    {
        var requestedName = new System.Reflection.AssemblyName(args.Name);
        var extensionDirectory = System.IO.Path.GetDirectoryName(typeof(ApexCodePackage).Assembly.Location);
        string assemblyPath = System.IO.Path.Combine(extensionDirectory, requestedName.Name + ".dll");

        if (System.IO.File.Exists(assemblyPath))
        {
            try
            {
                var asm = System.Reflection.Assembly.LoadFrom(assemblyPath);
                System.Diagnostics.Debug.WriteLine($"[AiAssistant.AssemblyResolve] Loaded {requestedName.Name} from {assemblyPath}");
                return asm;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AiAssistant.AssemblyResolve] Failed to load {assemblyPath}: {ex.Message}");
            }
        }
        return null;
    }

    public static Task<IServiceProvider> GetSystemServiceProviderAsync() 
    {
        // VSTHRD003: This TaskCompletionSource is created and resolved entirely within this package;
        // it is not a cross-context task. The warning is a false-positive — suppress it.
#pragma warning disable VSTHRD003
        return _serviceProviderTcs.Task;
#pragma warning restore VSTHRD003
    }

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        // AssemblyResolve is now hooked in the static constructor to ensure it runs as early as possible.

        await base.InitializeAsync(cancellationToken, progress);
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        try
        {
            Services.VsixLogger.Initialize(this);
            Services.VsixLogger.Log("Package InitializeAsync started.");

            // Register commands FIRST — menu must exist regardless of service health
            Services.VsixLogger.Log("Initializing Commands...");
            await ChatCommand.InitializeAsync(this);
            await SettingsCommand.InitializeAsync(this);
            // await LogCommand.InitializeAsync(this); // Hidden per user request

            Services.VsixLogger.Log("Commands initialized. Starting Service Initialization...");
            await InitializeServicesAsync();
            
            Services.VsixLogger.Log("Package Initialization Completed Successfully.");
        }
        catch (Exception ex)
        {
            Services.VsixLogger.LogError("InitializeAsync", ex);
            // Log to ActivityLog so the error is diagnosable
            LogErrorToActivityLog(ex);
            _serviceProviderTcs.TrySetException(ex);
            // Don't rethrow here — let the package finish loading with menu intact,
            // even if backend services failed. Optionally disable buttons via QueryStatus.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _serviceProvider?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void LogErrorToActivityLog(Exception ex)
    {
        try
        {
            if (GetService(typeof(SVsActivityLog)) is IVsActivityLog activityLog)
            {
                activityLog.LogEntry(
                    (uint)__ACTIVITYLOG_ENTRYTYPE.ALE_ERROR,
                    "ApexCode",
                    $"Package initialization failed: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            }
        }
        catch
        {
            // Last resort: ActivityLog itself failed — nothing more we can do
        }
    }

private async Task InitializeServicesAsync()
    {
        Services.VsixLogger.Log("Creating ServiceCollection...");
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging(configure => configure.AddDebug());
        services.AddSingleton<IAsyncServiceProvider>(this);

        Services.VsixLogger.Log("Setting up SQLite Database...");
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AiAssistant", "aiassistant.db");

        services.AddSingleton<IDbConnectionFactory>(new SqliteConnectionFactory(dbPath));
        services.AddSingleton<AiAssistantDbContext>();
        services.AddSingleton<ISessionRepository, SessionRepository>();
        services.AddSingleton<IMessageRepository, MessageRepository>();
        services.AddSingleton<IModelCacheRepository, ModelCacheRepository>();
        services.AddSingleton<IProviderProfileRepository, ProviderProfileRepository>();
        services.AddSingleton<ISystemPromptRepository, SystemPromptRepository>();
        services.AddSingleton<IDatabaseConnectionRepository, DatabaseConnectionRepository>();
        services.AddSingleton<AiAssistant.Storage.Repositories.ICheckpointRepository, AiAssistant.Storage.Repositories.CheckpointRepository>();
        services.AddSingleton<AiAssistant.Storage.Repositories.IEmbeddingSearchRepository, AiAssistant.Storage.Repositories.EmbeddingSearchRepository>();
        services.AddSingleton<AiAssistant.Storage.Services.IDefaultProviderSeeder, AiAssistant.Storage.Services.DefaultProviderSeeder>();

        Services.VsixLogger.Log("Registering core services...");
        services.AddSingleton<ISessionManager, SessionManager>();
        services.AddSingleton<IConversationHistory, ConversationHistory>();
        services.AddSingleton<IApprovalService, ApprovalService>();
        services.AddSingleton<AiAssistant.Core.Services.ITaskLockService, AiAssistant.Core.Services.TaskLockService>();
        
        // FIX Threading: Pre-resolve the client synchronously during service initialization
        // instead of using sync-over-async (.GetAwaiter().GetResult()) in the lambda.
        // ContextCompactor's summarization is called rarely (only during context compaction),
        // so we can afford to use a simple cached client resolution strategy.
        services.AddSingleton<IContextCompactor>(sp => {
            var factory = sp.GetRequiredService<AiAssistant.Llm.Services.IChatClientFactory>();
            
            // Simple resolver: always return the default client for summarization.
            // Summarization doesn't need to use the user's selected provider - it's an internal operation.
            // This avoids the async DB call entirely.
            Func<Microsoft.Extensions.AI.IChatClient> clientResolver = () => {
                return factory.GetDefaultClient();
            };

            return new ContextCompactor(clientResolver);
        });
        services.AddSingleton<ISystemPromptManager, SystemPromptManager>();
        var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiAssistant");
        services.AddSingleton<AiAssistant.Core.Services.IPlanService>(new AiAssistant.Core.Services.PlanWorkspaceService(appDataDir));
        services.AddSingleton<AiAssistant.Core.Services.IToolGatingService, AiAssistant.Core.Services.ToolGatingService>();
        services.AddSingleton<AiAssistant.Core.Services.ISettingsService, AiAssistant.Core.Services.SettingsService>();
        services.AddSingleton<AiAssistant.Storage.Repositories.IAppSettingsRepository, AiAssistant.Storage.Repositories.AppSettingsRepository>();
        services.AddSingleton<AiAssistant.Core.Services.IUserPreferencesService, AiAssistant.Core.Services.UserPreferencesService>();
        services.AddSingleton<ILogBus, LogBus>();
        services.AddSingleton<IOutputLogger, ApexCode.Services.OutputLogger>();
        services.AddSingleton<IStatusBarService, StatusBarService>();
        services.AddSingleton<IInfoBarService, InfoBarService>();
        services.AddSingleton<IPersonaService, PersonaService>();
        services.AddSingleton<IDatabaseConnectionService, DatabaseConnectionService>();
        services.AddSingleton<IVisualStudioEnvironmentService>(sp => new VisualStudioEnvironmentService(this, sp.GetRequiredService<IOutputLogger>()));
        services.AddSingleton<IProviderChangeNotifier, ProviderChangeNotifier>();
        services.AddSingleton<IModelFetchService, ModelFetchService>();
        services.AddSingleton<IFileBackupService, FileBackupService>();
        services.AddSingleton<AiAssistant.Core.Services.ICheckpointService, AiAssistant.Engine.Services.CheckpointService>();

        Services.VsixLogger.Log("Registering AI Chat Client Factory...");
        services.AddSingleton<AiAssistant.Llm.Services.IChatClientFactory>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<ChatClientFactory>>();
            var providerRepo = sp.GetRequiredService<IProviderProfileRepository>();
            var logBus = sp.GetRequiredService<ILogBus>();
            var modelCacheRepo = sp.GetRequiredService<IModelCacheRepository>();
            return new ChatClientFactory(logger, logBus, modelCacheRepo, providerId =>
            {
                try
                {
                    var profile = providerRepo.GetByIdAsync(providerId).GetAwaiter().GetResult();
                    return profile?.ApiKey;
                }
                catch (Exception ex)
                {
                    // FIX: Don't silently swallow API-key load failures — a DB/read error here
                    // otherwise surfaces to the user only as a confusing "no API key" message.
                    logger.LogWarning(ex, "Failed to resolve API key for provider {ProviderId}", providerId);
                    return null;
                }
            });
        });
        services.AddTransient<Microsoft.Extensions.AI.IChatClient>(sp => sp.GetRequiredService<AiAssistant.Llm.Services.IChatClientFactory>().GetDefaultClient());

        Services.VsixLogger.Log("Registering Code Editing Services...");
        services.AddSingleton<IErrorFixer, LlmErrorFixer>();
        services.AddSingleton<IRoslynEditService, RoslynEditService>();
        services.AddSingleton<ICompileValidator, CompileValidator>();
        services.AddSingleton<IDiffPreviewService, DiffPreviewService>();
        services.AddSingleton<AutoHealingLoop>();

        Services.VsixLogger.Log("Registering Deterministic Pipeline Services (Phase 4)...");
        services.AddSingleton<ApexCode.Services.VsWorkspaceContextGatherer>();
        services.AddSingleton<AiAssistant.Core.Services.IEditApplicationService, ApexCode.Services.VsEditApplicationService>();
        services.AddSingleton<AiAssistant.Engine.Pipeline.DeterministicPipelineOrchestrator>();
        services.AddSingleton<AiAssistant.Engine.Context.IWorkspaceContextGatherer, AiAssistant.Engine.Context.WorkspaceContextGatherer>();
        services.AddSingleton<AiAssistant.Engine.Services.DeterministicEditEngine>(); // In case needed
        // Use improved resolver with Roslyn-based dependency resolution
        services.AddSingleton<AiAssistant.Core.Services.IFileDependencyResolver, AiAssistant.Engine.Services.ImprovedFileDependencyResolver>();
        services.AddSingleton<AiAssistant.Core.Services.IFileReadTracker, ApexCode.Services.FileReadTracker>();
        services.AddSingleton<AiAssistant.Core.Services.ITransactionSnapshotService, AiAssistant.Engine.Services.TransactionSnapshotService>();

        services.AddSingleton<Func<CancellationToken, Task<string?>>>(sp =>
        {
            var vsEnv = sp.GetRequiredService<IVisualStudioEnvironmentService>();
            return async (ct) => await vsEnv.GetWorkspaceRootAsync(ct);
        });

        Services.VsixLogger.Log("Registering Tools...");
        services.AddSingleton<IToolProvider, AiAssistant.Tools.Functions.ReadFileLinesFunction>();
        services.AddSingleton<IToolProvider, AiAssistant.Tools.Functions.CreateFileFunction>();
        services.AddSingleton<IToolProvider, AiAssistant.Tools.Functions.ReplaceFileFunction>();
        services.AddSingleton<IToolProvider, AiAssistant.Tools.Functions.SearchCodeFunction>();
        services.AddSingleton<CommandSessionService>();
        services.AddSingleton<IToolProvider, AiAssistant.Tools.Functions.ReadCommandOutputFunction>();
        services.AddSingleton<IToolProvider, AiAssistant.Tools.Functions.StopCommandFunction>();
        services.AddSingleton<IToolProvider, AiAssistant.Tools.Functions.ExecuteCommandFunction>();
        services.AddSingleton<IToolProvider, AiAssistant.Tools.Functions.GetDiagnosticsFunction>();
        services.AddSingleton<IToolProvider, AiAssistant.Tools.Functions.GetWorkspaceInfoFunction>();
        services.AddSingleton<IToolProvider, AiAssistant.Tools.Functions.ListFilesFunction>();
        // FIX: ListWorkspaceFilesFunction was implemented but never registered, so the model could not
        // call 'list_workspace_files' to enumerate solution files. Register it now.
        services.AddSingleton<IToolProvider, AiAssistant.Tools.Functions.ListWorkspaceFilesFunction>();
        // PRE-EXISTING BUG FIX: FindUsagesFunction now lives in AiAssistant.Tools.Functions.
        services.AddSingleton<IToolProvider, AiAssistant.Tools.Functions.FindUsagesFunction>();
        services.AddSingleton<IToolProvider, AiAssistant.Tools.Functions.GetSymbolReferencesFunction>();
        services.AddSingleton<IToolProvider, AiAssistant.Tools.Functions.GetTypeHierarchyFunction>();
        // Database tools resolve their shared executor through DI, without global query callbacks.
        services.AddSingleton<IToolProvider, AiAssistant.Tools.Functions.GetDatabaseSchemaFunction>();
        services.AddSingleton<IToolProvider, AiAssistant.Tools.Functions.ExecuteDatabaseQueryFunction>();
        services.AddSingleton<IToolProvider, AiAssistant.Tools.Functions.CheckDatabaseConnectionFunction>();
        
        // Phase 2 New Tools
        services.AddTransient<IToolProvider, AiAssistant.Tools.Functions.DeleteFileFunction>();
        services.AddTransient<IToolProvider, AiAssistant.Tools.Functions.RenameFileFunction>();
        services.AddTransient<IToolProvider, AiAssistant.Tools.Functions.GetActiveDocumentFunction>();
        services.AddTransient<IToolProvider, AiAssistant.Tools.Functions.RunTestsFunction>();
        services.AddTransient<IToolProvider, AiAssistant.Tools.Functions.RenameSymbolFunction>();
        services.AddTransient<IToolProvider, AiAssistant.Tools.Functions.RevertTransactionFunction>();

        // Enhanced Plan Mode Tools
        services.AddTransient<IToolProvider, AiAssistant.Tools.Functions.Plan.ProposePlanTool>();
        services.AddTransient<IToolProvider, AiAssistant.Tools.Functions.Plan.RevisePlanTool>();
        services.AddTransient<IToolProvider, AiAssistant.Tools.Functions.Act.StartTaskTool>();
        services.AddTransient<IToolProvider, AiAssistant.Tools.Functions.Act.GenerateWalkthroughTool>();
        services.AddTransient<IToolProvider, AiAssistant.Tools.Functions.Act.GenerateTasksTool>();
        services.AddTransient<IToolProvider, AiAssistant.Tools.Functions.Act.CompleteTaskTool>();
        services.AddTransient<IToolProvider, AiAssistant.Tools.Functions.Act.AskQuestionTool>();
        services.AddTransient<IToolProvider, AiAssistant.Tools.Functions.Act.AttemptCompletionTool>();
        services.AddTransient<IToolProvider, AiAssistant.Tools.Functions.GetFileSkeletonFunction>();
        services.AddSingleton<AiAssistant.Tools.Services.IToolRegistry, ToolRegistry>();
        services.AddChatRequestPipeline();

        // Semantic Search
        Services.VsixLogger.Log("Registering Semantic Search & Embeddings...");
        services.AddSingleton<ISemanticSearchIndexer, SemanticSearchIndexer>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        services.AddSingleton<AiAssistant.Core.Services.IEmbeddingProvider>(sp => 
            new ApexCode.Services.BgeEmbeddingProvider(Path.Combine(appData, "AiAssistant", "EmbeddingModel")));
        services.AddSingleton<AiAssistant.Engine.Services.ModelDownloaderService>();
        services.AddSingleton<IndexingService>();

        services.AddSingleton<IChatService, ChatService>();        
        services.AddSingleton<ApexCode.Tools.DatabaseQueryToolExecutor>();
        services.AddSingleton<IDatabaseToolExecutor>(sp => sp.GetRequiredService<ApexCode.Tools.DatabaseQueryToolExecutor>());
        services.AddSingleton<AiAssistant.Core.Services.IEnhancedPlanOrchestrator, AiAssistant.Core.Services.EnhancedPlanOrchestrator>();

        Services.VsixLogger.Log("Building Service Provider...");
        _serviceProvider = services.BuildServiceProvider();
        SystemServiceProvider = _serviceProvider;

        var indexingSvc = _serviceProvider.GetRequiredService<IndexingService>();
        _ = Task.Run(async () =>
        {
            try { await indexingSvc.StartAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApexCode] IndexingService failed to start: {ex.Message}");
            }
        });

        Services.VsixLogger.Log("Setting up Tool Registry...");
        // Register all AI tools with the tool registry
        var toolRegistry = _serviceProvider.GetRequiredService<AiAssistant.Tools.Services.IToolRegistry>();
        var registeredTools = _serviceProvider.GetServices<IToolProvider>();
        foreach (var tool in registeredTools)
        {
            toolRegistry.RegisterToolProvider(tool);
        }

        Services.VsixLogger.Log("Service Provider Built and Tools Registered.");


        var dbContext = _serviceProvider.GetRequiredService<AiAssistantDbContext>();
        try
        {
            await dbContext.InitializeAsync();
            var seeder = _serviceProvider.GetRequiredService<AiAssistant.Storage.Services.IDefaultProviderSeeder>();
            await seeder.SeedAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] Database initialization failed: {ex.Message}");
            try
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(typeof(SVsActivityLog)) is IVsActivityLog activityLog)
                {
                    activityLog.LogEntry(
                        (uint)__ACTIVITYLOG_ENTRYTYPE.ALE_ERROR,
                        "ApexCode",
                        $"Database initialization failed: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
                }
            }
            catch { /* ActivityLog failed */ }
        }

        // Restore active session from settings file
        await RestoreActiveSessionAsync(_serviceProvider);

        // Wire up UI dialogs for approvals
        var approvalService = _serviceProvider.GetRequiredService<IApprovalService>();
        approvalService.ApprovalRequested -= ApprovalService_ApprovalRequested;
        approvalService.ApprovalRequested += ApprovalService_ApprovalRequested;

        var dbQueryTool = _serviceProvider.GetRequiredService<ApexCode.Tools.DatabaseQueryToolExecutor>();
        AiAssistant.Tools.Functions.CheckDatabaseConnectionFunction.ConnectionTester = dbQueryTool.CheckConnectionAsync;

        // Wire up settings change to clear IChatClientFactory cache
        var settingsSvc = _serviceProvider.GetRequiredService<AiAssistant.Core.Services.ISettingsService>();
        var chatFactory = _serviceProvider.GetRequiredService<AiAssistant.Llm.Services.IChatClientFactory>();
        settingsSvc.ProviderSettingsChanged += (s, e) => chatFactory.InvalidateCache();

        // Finally, unblock any waiting tool windows now that everything (including DB) is initialized
        _serviceProviderTcs.TrySetResult(_serviceProvider);

        // Initialize Checkpoint system
        _ = Task.Run(async () =>
        {
            try
            {
                var checkpointService = _serviceProvider.GetRequiredService<AiAssistant.Core.Services.ICheckpointService>();
                var envService = _serviceProvider.GetRequiredService<IVisualStudioEnvironmentService>();
                
                var initWorkspace = async () =>
                {
                    var solutionDir = await envService.GetWorkspaceRootAsync();
                    if (!string.IsNullOrEmpty(solutionDir))
                    {
                        await checkpointService.InitializeAsync(solutionDir, CancellationToken.None);
                        
                        var transactionService = _serviceProvider.GetRequiredService<ITransactionSnapshotService>();
                        if (transactionService.HasOrphanedTransactions(solutionDir))
                        {
                            var uiShell = _serviceProvider.GetService<SVsUIShell>() as IVsUIShell;
                            if (uiShell != null)
                            {
                                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                                int result = 0;
                                Guid emptyGuid = Guid.Empty;
                                uiShell.ShowMessageBox(
                                    0,
                                    ref emptyGuid,
                                    "AiAssistant Recovery",
                                    "AiAssistant crashed while applying an edit. Would you like to revert the incomplete changes?",
                                    string.Empty,
                                    0,
                                    OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST,
                                    OLEMSGICON.OLEMSGICON_WARNING,
                                    0,
                                    out result);

                                if (result == 6) // IDYES
                                {
                                    await transactionService.RevertOrphanedTransactionsAsync(solutionDir);
                                }
                                else
                                {
                                    transactionService.CleanupOrphanedTransactions(solutionDir);
                                }
                            }
                        }
                    }
                };

                envService.WorkspaceChanged += async (s, e) => await initWorkspace();
                await initWorkspace();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApexCode] Checkpoint initialization failed: {ex.Message}");
            }
        });
    }

    private void ApprovalService_ApprovalRequested(object sender, AiAssistant.Core.Models.ApprovalRequest request)
    {
        if (sender is not IApprovalService approvalService) return;
        _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var settingsService = SystemServiceProvider?.GetService(typeof(AiAssistant.Core.Services.ISettingsService)) as AiAssistant.Core.Services.ISettingsService;
                var outputLogger = _serviceProvider?.GetService(typeof(AiAssistant.Core.Services.IOutputLogger)) as AiAssistant.Core.Services.IOutputLogger;

                // Let ChatPanel handle the UI inline. All conditional allow/deny logic
                // is now handled natively within each individual tool function.
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApexCode] Approval dialog failed: {ex.Message}");
                try
                {
                    await approvalService.RejectAsync(request.Id);
                }
                catch (Exception inner)
                {
                    System.Diagnostics.Debug.WriteLine($"[ApexCode] RejectAsync also failed: {inner.Message}");
                }
            }
        });
    }

    #region Session Persistence

    private static string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AiAssistant", "settings.json");

    private async Task RestoreActiveSessionAsync(IServiceProvider services)
    {
        try
        {
            if (!File.Exists(SettingsFilePath)) return;
            var json = await Task.Run(() => File.ReadAllText(SettingsFilePath));
            var settings = JsonSerializer.Deserialize<SessionSettings>(json);
            if (settings == null || string.IsNullOrEmpty(settings.ActiveSessionId)) return;

            var sessionManager = services.GetRequiredService<ISessionManager>();
            var session = await sessionManager.GetSessionAsync(settings.ActiveSessionId);
            if (session != null)
            {
                await sessionManager.SetActiveSessionAsync(settings.ActiveSessionId);
                System.Diagnostics.Debug.WriteLine($"[ApexCode] Restored active session: {settings.ActiveSessionId}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] Failed to restore session: {ex.Message}");
        }
    }

    public static async Task SaveActiveSessionAsync(string? sessionId)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var settings = new SessionSettings { ActiveSessionId = sessionId ?? "" };
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            await Task.Run(() => AiAssistant.Storage.SafeFileWriter.WriteAllText(SettingsFilePath, json));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] Failed to save session: {ex.Message}");
        }
    }

    #endregion
}

internal class SessionSettings
{
    public string ActiveSessionId { get; set; } = "";
}
