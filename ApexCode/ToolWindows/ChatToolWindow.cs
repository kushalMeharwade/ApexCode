using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using AiAssistant.Core.Services;
using AiAssistant.Storage.Repositories;
using AiAssistant.UI.Controls;
using Microsoft.VisualStudio.Shell;

namespace ApexCode.ToolWindows;

[Guid("a1b2c3d4-e5f6-7890-abcd-ef1234567890")]
public class ChatToolWindow : ToolWindowPane
{
    private ChatPanel? _chatPanel;

    public ChatToolWindow() : base(null)
    {
        Caption = "ApexCode";
        Content = CreateLoadingContent();
    }

    protected override void Initialize()
    {
        base.Initialize();
        // FIX: Use a proper joinable task instead of FileAndForget so that exceptions thrown
        // BEFORE InitializeAsync's own try/catch (e.g. during GetSystemServiceProviderAsync)
        // are surfaced rather than silently swallowed. VSSDK007 is suppressed here because
        // this is the documented pattern for tool-window async init — the task is tracked by
        // JoinableTaskFactory and VS will wait for it during shutdown.
#pragma warning disable VSSDK007
#pragma warning disable VSTHRD110 // The lambda's try/catch observes errors; no further result observance needed
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await InitializeAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApexCode] ChatToolWindow outer init failed: {ex}");
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Content = CreateErrorContent($"Failed to initialize ApexCode: {ex.Message}");
            }
        });
#pragma warning restore VSTHRD110
#pragma warning restore VSSDK007
    }

    private async System.Threading.Tasks.Task InitializeAsync()
    {
        try
        {
            // VSTHRD003: Awaiting a TaskCompletionSource task from GetSystemServiceProviderAsync is the
            // correct pattern here — the TCS is created in the same AppDomain and resolved by the
            // package's own InitializeAsync. This suppression is intentional.
#pragma warning disable VSTHRD003
            var sp = await ApexCodePackage.GetSystemServiceProviderAsync();
#pragma warning restore VSTHRD003
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var chatService = sp.GetService(typeof(IChatService)) as IChatService;
            var personaService = sp.GetService(typeof(IPersonaService)) as IPersonaService;
            var diffService = sp.GetService(typeof(AiAssistant.Engine.Services.IDiffPreviewService)) as AiAssistant.Engine.Services.IDiffPreviewService;
            var telemetryService = sp.GetService(typeof(AiAssistant.Storage.Services.ITelemetryService)) as AiAssistant.Storage.Services.ITelemetryService;
            var settingsService = sp.GetService(typeof(AiAssistant.Core.Services.ISettingsService)) as AiAssistant.Core.Services.ISettingsService;
            var providerRepo = sp.GetService(typeof(AiAssistant.Storage.Repositories.IProviderProfileRepository)) as AiAssistant.Storage.Repositories.IProviderProfileRepository;
            var backupService = sp.GetService(typeof(AiAssistant.Core.Services.IFileBackupService)) as AiAssistant.Core.Services.IFileBackupService;
            var providerChangeNotifier = sp.GetService(typeof(IProviderChangeNotifier)) as IProviderChangeNotifier;
            var dependencyResolver = sp.GetService(typeof(AiAssistant.Core.Services.IFileDependencyResolver)) as AiAssistant.Core.Services.IFileDependencyResolver;
            var modelFetchService = sp.GetService(typeof(IModelFetchService)) as IModelFetchService;
            var modelCacheRepo = sp.GetService(typeof(AiAssistant.Storage.Repositories.IModelCacheRepository)) as AiAssistant.Storage.Repositories.IModelCacheRepository;
            var promptRepo = sp.GetService(typeof(ISystemPromptRepository)) as ISystemPromptRepository;
            var dbService = sp.GetService(typeof(IDatabaseConnectionService)) as IDatabaseConnectionService;
            var chatClientFactory = sp.GetService(typeof(AiAssistant.Llm.Services.IChatClientFactory)) as AiAssistant.Llm.Services.IChatClientFactory;
            var outputLogger = sp.GetService(typeof(IOutputLogger)) as IOutputLogger;
            var vsEnvService = sp.GetService(typeof(IVisualStudioEnvironmentService)) as IVisualStudioEnvironmentService;
            var indexingService = sp.GetService(typeof(AiAssistant.Engine.Services.IndexingService)) as AiAssistant.Engine.Services.IndexingService;
            var modelDownloader = sp.GetService(typeof(AiAssistant.Engine.Services.ModelDownloaderService)) as AiAssistant.Engine.Services.ModelDownloaderService;
            var approvalService = sp.GetService(typeof(IApprovalService)) as IApprovalService;
            var logBus = sp.GetService(typeof(ILogBus)) as ILogBus;
            var checkpointService = sp.GetService(typeof(AiAssistant.Core.Services.ICheckpointService)) as AiAssistant.Core.Services.ICheckpointService;
            var planService = sp.GetService(typeof(AiAssistant.Core.Services.IPlanService)) as AiAssistant.Core.Services.IPlanService;

            _chatPanel = new ChatPanel(chatService, personaService, diffService, telemetryService, settingsService, providerRepo, backupService, providerChangeNotifier, modelFetchService, modelCacheRepo, indexingService, promptRepo, dbService, chatClientFactory, outputLogger, vsEnvService, modelDownloader, approvalService, logBus, checkpointService, dependencyResolver: dependencyResolver, planService: planService);

            Content = _chatPanel;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] ChatToolWindow init failed: {ex.Message}");
            Content = CreateErrorContent($"Failed to initialize ApexCode: {ex.Message}");
        }
    }

    private static UIElement CreateLoadingContent()
    {
        var grid = new Grid { Background = SystemColors.WindowBrush };
        grid.Children.Add(new TextBlock
        {
            Text = "Loading ApexCode...",
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = SystemColors.ControlTextBrush,
            FontSize = 14
        });
        return grid;
    }

    private static UIElement CreateErrorContent(string message)
    {
        var grid = new Grid { Background = SystemColors.WindowBrush };
        grid.Children.Add(new TextBlock
        {
            Text = message,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = SystemColors.GrayTextBrush,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20)
        });
        return grid;
    }

    public ChatPanel? ChatPanel => _chatPanel;
}


