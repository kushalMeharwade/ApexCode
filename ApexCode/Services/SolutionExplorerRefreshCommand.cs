using System;
using Microsoft.VisualStudio.OLE.Interop;

namespace ApexCode.Services;

internal static class SolutionExplorerRefreshCommand
{
    // Open Folder has its own explorer and command set. Verified against
    // Microsoft.VisualStudio.Workspace.VSIntegration PackageIds and ViewWorkspaceModel:
    // cmdid_Refresh (0x20B) calls ExecuteRefreshCommand, unlike VSStd97.Refresh.
    internal static readonly Guid FolderWindow = new Guid("62c002de-32b0-4b6e-8193-07475dfc5df5");
    internal static readonly Guid FolderCommandSet = new Guid("cfb400f1-5c60-4f3c-856e-180d28def0b7");
    internal const uint FolderRefresh = 0x20B;
    private static readonly Guid StandardCommandSet97 = new Guid("5efc7975-14bc-11cf-9b2b-00aa00573819");
    private const uint StandardRefresh = 189;

    internal static bool TryExecute(IOleCommandTarget target, bool folderView)
    {
        var group = folderView ? FolderCommandSet : StandardCommandSet97;
        var command = folderView ? FolderRefresh : StandardRefresh;
        var status = new[] { new OLECMD { cmdID = command } };
        if (target.QueryStatus(ref group, 1, status, IntPtr.Zero) < 0 ||
            (status[0].cmdf & (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED)) !=
            (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED)) return false;
        return target.Exec(ref group, command, 0, IntPtr.Zero, IntPtr.Zero) >= 0;
    }
}
