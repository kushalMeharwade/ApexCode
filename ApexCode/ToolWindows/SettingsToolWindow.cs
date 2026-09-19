using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell;
using AiAssistant.Core.Services;
using AiAssistant.Llm.Services;
using AiAssistant.Storage.Repositories;
using AiAssistant.UI.Controls;

namespace ApexCode.ToolWindows;

/// <summary>
/// Tool window that hosts the SettingsView with DI services injected.

/// </summary>
[Guid("b2c3d4e5-f6a7-8901-bcde-f12345678901")]
public class SettingsToolWindow : ToolWindowPane
{
    public SettingsToolWindow() : base(null)
    {
        Caption = "ApexCode AI Settings";
        Content = CreateLoadingContent();
    }

    protected override void Initialize()
    {
        base.Initialize();
        _ = InitializeAsync();
    }

    private async System.Threading.Tasks.Task InitializeAsync()
    {
        try
        {
            var sp = await ApexCodePackage.GetSystemServiceProviderAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var providerRepo = sp.GetService(typeof(IProviderProfileRepository)) as IProviderProfileRepository;
            var promptRepo = sp.GetService(typeof(ISystemPromptRepository)) as ISystemPromptRepository;
            var personaService = sp.GetService(typeof(IPersonaService)) as IPersonaService;
            var dbService = sp.GetService(typeof(IDatabaseConnectionService)) as IDatabaseConnectionService;
            var modelCacheRepo = sp.GetService(typeof(IModelCacheRepository)) as IModelCacheRepository;
            var chatClientFactory = sp.GetService(typeof(AiAssistant.Llm.Services.IChatClientFactory)) as AiAssistant.Llm.Services.IChatClientFactory;
            var settingsService = sp.GetService(typeof(ISettingsService)) as ISettingsService;
            var telemetryService = sp.GetService(typeof(AiAssistant.Storage.Services.ITelemetryService)) as AiAssistant.Storage.Services.ITelemetryService;
            var outputLogger = sp.GetService(typeof(IOutputLogger)) as IOutputLogger;
            var providerChangeNotifier = sp.GetService(typeof(IProviderChangeNotifier)) as IProviderChangeNotifier;
            var modelFetchService = sp.GetService(typeof(IModelFetchService)) as IModelFetchService;

            var settingsView = new SettingsView(providerRepo, promptRepo, personaService, dbService, modelCacheRepo, chatClientFactory, telemetryService, settingsService, outputLogger, providerChangeNotifier, modelFetchService);
            Content = settingsView;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] SettingsToolWindow init failed: {ex.Message}");
            Content = CreateErrorContent($"Failed to load settings: {ex.Message}");
        }
    }

    private static UIElement CreateLoadingContent()
    {
        var grid = new Grid { Background = SystemColors.WindowBrush };
        grid.Children.Add(new TextBlock
        {
            Text = "Loading Settings...",
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
}


