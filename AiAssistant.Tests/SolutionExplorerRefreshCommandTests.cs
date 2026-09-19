using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using ApexCode.Services;
using Xunit;

namespace AiAssistant.Tests;

public class SolutionExplorerRefreshCommandTests
{
    [Fact]
    public void FolderViewUsesItsOwnToolbarCommand()
    {
        var target = new FolderTarget();
        Assert.False(SolutionExplorerRefreshCommand.TryExecute(target, false));
        Assert.True(SolutionExplorerRefreshCommand.TryExecute(target, true));
        Assert.Equal(1, target.Executions);
    }

    [Fact]
    public void DisabledOrFailedRefreshIsNotReportedAsSuccessful()
    {
        var target = new FolderTarget { Enabled = false };
        Assert.False(SolutionExplorerRefreshCommand.TryExecute(target, true));
        Assert.Equal(0, target.Executions);
        target.Enabled = true;
        target.Result = VSConstants.E_FAIL;
        Assert.False(SolutionExplorerRefreshCommand.TryExecute(target, true));
    }

    private sealed class FolderTarget : IOleCommandTarget
    {
        public bool Enabled = true;
        public int Result = VSConstants.S_OK;
        public int Executions;
        public int QueryStatus(ref Guid group, uint count, OLECMD[] commands, IntPtr text)
        {
            if (group != new Guid("cfb400f1-5c60-4f3c-856e-180d28def0b7") || commands[0].cmdID != 523)
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            commands[0].cmdf = (uint)OLECMDF.OLECMDF_SUPPORTED | (Enabled ? (uint)OLECMDF.OLECMDF_ENABLED : 0);
            return VSConstants.S_OK;
        }
        public int Exec(ref Guid group, uint id, uint options, IntPtr input, IntPtr output)
        {
            Assert.Equal(new Guid("cfb400f1-5c60-4f3c-856e-180d28def0b7"), group);
            Assert.Equal(523u, id);
            Executions++;
            return Result;
        }
    }
}
