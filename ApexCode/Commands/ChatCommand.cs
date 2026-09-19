using System;
using System.IO;
using System.ComponentModel.Design;
using System.Threading;
using ApexCode.ToolWindows;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ApexCode.Commands;

internal sealed class ChatCommand
{
    public const int CommandId = 0x0100;
    public static readonly Guid CommandSet = new("c5e8b1d9-7e2f-4a3b-9c4d-8b6a5e4d3c2b");

    private readonly AsyncPackage _package;

    private ChatCommand(AsyncPackage package, OleMenuCommandService commandService)
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
            // Log and return gracefully instead of crashing
            System.Diagnostics.Debug.WriteLine("[ApexCode] OleMenuCommandService not available — command not registered");
            return;
        }
        new ChatCommand(package, commandService);
    }

    private void Execute(object sender, EventArgs e)
    {
        _ = _package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await _package.ShowToolWindowAsync(
                    typeof(ChatToolWindow),
                    0,
                    true,
                    _package.DisposalToken);
            }
            catch (OperationCanceledException)
            {
                // User cancelled — this is expected, do nothing
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApexCode] Failed to show tool window: {ex.Message}");
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (ServiceProvider.GlobalProvider.GetService(typeof(SVsActivityLog)) is IVsActivityLog activityLog)
                    {
                        activityLog.LogEntry(
                            (uint)__ACTIVITYLOG_ENTRYTYPE.ALE_ERROR,
                            "AiAssistant.Vsix",
                            $"Failed to open chat tool window: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
                    }
                }
                catch { /* ActivityLog failed — nothing more we can do */ }
            }
        });
    }
}

