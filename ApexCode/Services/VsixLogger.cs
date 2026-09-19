using System;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ApexCode.Services;

public static class VsixLogger
{
    private static IVsOutputWindowPane _pane;
    private static Guid PaneGuid = new Guid("11111111-2222-3333-4444-555555555555");
    private const string PaneTitle = "ApexCode";

    public static void Initialize(IServiceProvider serviceProvider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_pane != null) return;

        if (serviceProvider.GetService(typeof(SVsOutputWindow)) is IVsOutputWindow output)
        {
            output.CreatePane(ref PaneGuid, PaneTitle, 1, 1);
            output.GetPane(ref PaneGuid, out _pane);
            _pane?.Activate(); // Bring to front when initializing
        }
    }

    public static void Log(string message)
    {
        try
        {
            if (_pane != null)
            {
                // OutputStringThreadSafe is safe to call from any thread — drop the dead if/else
                // that previously had identical branches (and which triggered VSTHRD010 because the
                // analyser saw the _pane field access on a non-UI thread).
#pragma warning disable VSTHRD010 // OutputStringThreadSafe is explicitly thread-safe per the VS SDK docs
                _pane.OutputStringThreadSafe($"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
#pragma warning restore VSTHRD010
            }
            // Always mirror to the debug trace for diagnostics even when the pane is not ready.
            System.Diagnostics.Debug.WriteLine($"[ApexCode] {message}");
        }
        catch
        {
            // Best-effort logging — never throw from a logger.
        }
    }

    public static void LogError(string context, Exception ex)
    {
        Log($"ERROR [{context}]: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
    }
}
