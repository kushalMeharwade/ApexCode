using System;
using System.Threading;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ApexCode.Commands;


internal sealed class SettingsCommand
{
    public const int CommandId = 0x0200;
    public static readonly Guid CommandSet = new("c5e8b1d9-7e2f-4a3b-9c4d-8b6a5e4d3c2b");

    private readonly AsyncPackage _package;

    private SettingsCommand(AsyncPackage package, OleMenuCommandService commandService)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));
        if (commandService == null)
            throw new ArgumentNullException(nameof(commandService));

        var cmdId = new CommandID(CommandSet, CommandId);
        var cmd = new OleMenuCommand(Execute, cmdId);
        commandService.AddCommand(cmd);
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        if (package == null)
            throw new ArgumentNullException(nameof(package));

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        if (commandService == null)
        {
            System.Diagnostics.Debug.WriteLine("[ApexCode] OleMenuCommandService not available — settings command not registered");
            return;
        }
        new SettingsCommand(package, commandService);
    }

    private void Execute(object sender, EventArgs e)
    {
        _ = _package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var window = (ToolWindowPane)_package.FindToolWindow(typeof(ToolWindows.SettingsToolWindow), 0, true);
                if (window == null)
                {
                    throw new NotSupportedException("Cannot create Settings tool window.");
                }
                var windowFrame = (IVsWindowFrame)window.Frame;
                if (windowFrame == null)
                {
                    throw new NotSupportedException("Cannot get Settings tool window frame.");
                }
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(windowFrame.Show());
            }
            catch (OperationCanceledException)
            {
                // User cancelled — expected
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApexCode] Failed to open settings: {ex.Message}");
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (ServiceProvider.GlobalProvider.GetService(typeof(SVsActivityLog)) is IVsActivityLog activityLog)
                    {
                        activityLog.LogEntry(
                            (uint)__ACTIVITYLOG_ENTRYTYPE.ALE_ERROR,
                            "AiAssistant.Vsix",
                            $"Failed to open settings: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
                    }
                }
                catch { /* ActivityLog failed */ }
            }
        });
    }
}


