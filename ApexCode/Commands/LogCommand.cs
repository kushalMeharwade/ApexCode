using System;
using System.ComponentModel.Design;
using System.Threading;
using ApexCode.ToolWindows;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ApexCode.Commands;

internal sealed class LogCommand
{
    public const int CommandId = 0x0102;
    public static readonly Guid CommandSet = new("c5e8b1d9-7e2f-4a3b-9c4d-8b6a5e4d3c2b");

    private readonly AsyncPackage _package;

    private LogCommand(AsyncPackage package, OleMenuCommandService commandService)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));
        if (commandService == null)
            throw new ArgumentNullException(nameof(commandService), "OleMenuCommandService is not available");
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
            System.Diagnostics.Debug.WriteLine("[ApexCode] OleMenuCommandService not available — LogCommand not registered");
            return;
        }
        new LogCommand(package, commandService);
    }

    private void Execute(object sender, EventArgs e)
    {
        _ = _package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await _package.ShowToolWindowAsync(
                    typeof(LogToolWindow),
                    0,
                    true,
                    _package.DisposalToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApexCode] Failed to show log tool window: {ex.Message}");
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (ServiceProvider.GlobalProvider.GetService(typeof(SVsActivityLog)) is IVsActivityLog activityLog)
                    {
                        activityLog.LogEntry(
                            (uint)__ACTIVITYLOG_ENTRYTYPE.ALE_ERROR,
                            "AiAssistant.Vsix",
                            $"Failed to open log tool window: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
                    }
                }
                catch { }
            }
        });
    }
}
