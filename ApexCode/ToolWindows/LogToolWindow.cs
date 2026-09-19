using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using AiAssistant.Core.Services;
using AiAssistant.UI.Controls;
using Microsoft.VisualStudio.Shell;

namespace ApexCode.ToolWindows;

[Guid("B1B2C3D4-E5F6-7890-ABCD-EF1234567891")]
public class LogToolWindow : ToolWindowPane
{
    private LogPanel? _logPanel;

    public LogToolWindow() : base(null)
    {
        Caption = "ApexCode Logs";
        Content = CreateLoadingContent();
    }

    protected override void Initialize()
    {
        base.Initialize();
#pragma warning disable VSSDK007
#pragma warning disable VSTHRD110 
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await InitializeAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApexCode] LogToolWindow init failed: {ex}");
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Content = CreateErrorContent($"Failed to initialize logs: {ex.Message}");
            }
        });
#pragma warning restore VSTHRD110
#pragma warning restore VSSDK007
    }

    private async System.Threading.Tasks.Task InitializeAsync()
    {
        var sp = await ApexCodePackage.GetSystemServiceProviderAsync();
        if (sp == null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Content = CreateErrorContent("Service provider not available.");
            return;
        }

        var logBus = sp.GetService(typeof(ILogBus)) as ILogBus;

        if (logBus == null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Content = CreateErrorContent("Logging service is not available.");
            return;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        _logPanel = new LogPanel(logBus);
        Content = _logPanel;
    }

    private object CreateLoadingContent()
    {
        return new TextBlock
        {
            Text = "Loading AI Logs...",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20)
        };
    }

    private object CreateErrorContent(string message)
    {
        return new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.Red,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20)
        };
    }
}
