using System;
using System.Threading.Tasks;
using AiAssistant.Core.Services;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ApexCode.Services;

public class OutputLogger : IOutputLogger
{
    private readonly ISettingsService _settingsService;
    private readonly Microsoft.VisualStudio.Shell.IAsyncServiceProvider _serviceProvider;
    private readonly ILogBus _logBus;
    private IVsOutputWindowPane _pane;
    private static readonly Guid PaneGuid = new Guid("4A7E0BD1-6F27-46B9-B330-84E3F3B1A24B"); // Unique GUID for PEKKA pane

    public OutputLogger(ISettingsService settingsService, Microsoft.VisualStudio.Shell.IAsyncServiceProvider serviceProvider, ILogBus logBus)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logBus = logBus ?? throw new ArgumentNullException(nameof(logBus));
    }

    private async Task<IVsOutputWindowPane> GetPaneAsync()
    {
        if (_pane != null) return _pane;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        
        var outputWindow = await _serviceProvider.GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow;
        if (outputWindow == null) return null;

        Guid paneGuid = PaneGuid;
        outputWindow.GetPane(ref paneGuid, out _pane);
        
        if (_pane == null)
        {
            outputWindow.CreatePane(ref paneGuid, "ApexCode", 1, 1);
            outputWindow.GetPane(ref paneGuid, out _pane);
        }

        return _pane;
    }

    public async Task LogAsync(string message)
    {
        await LogAsync(LogCategory.Agent, message);
    }

    public void Log(string message)
    {
        Log(LogCategory.Agent, message);
    }

    public async Task LogAsync(LogCategory category, string message, string? detail = null)
    {
        if (_settingsService == null || !_settingsService.EnableLogs) return;

        try
        {
            // Publish to the event bus for the WPF tool window
            _logBus.Publish(new LogEntry(DateTime.Now, category, message, detail));

            // Also write to the VS Output pane
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var pane = await GetPaneAsync();
            if (pane != null)
            {
                var prefix = category switch
                {
                    LogCategory.Chat => "[CHAT]",
                    LogCategory.Llm => "[LLM]",
                    LogCategory.Tool => "[TOOL]",
                    LogCategory.Settings => "[SETTINGS]",
                    _ => "[AGENT]"
                };
                
                pane.OutputStringThreadSafe($"[{DateTime.Now:HH:mm:ss.fff}] {prefix} {message}\n");
                if (detail != null)
                {
                    pane.OutputStringThreadSafe($"{detail}\n");
                }
            }
        }
        catch { /* Ignore logging errors */ }
    }

    public void Log(LogCategory category, string message, string? detail = null)
    {
        if (_settingsService == null || !_settingsService.EnableLogs) return;
        _ = LogAsync(category, message, detail);
    }
}
