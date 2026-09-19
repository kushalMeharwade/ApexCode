using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ApexCode.Services;

public interface IStatusBarService
{
    void SetText(string text);
    void Clear();
}

public class StatusBarService : IStatusBarService
{
    private readonly IServiceProvider _serviceProvider;

    public StatusBarService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public void SetText(string text)
    {
        ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_serviceProvider.GetService(typeof(SVsStatusbar)) is IVsStatusbar statusBar)
            {
                int frozen;
                statusBar.IsFrozen(out frozen);
                if (frozen == 0)
                {
                    statusBar.SetText(text);
                }
            }
        });
    }

    public void Clear()
    {
        SetText(string.Empty);
    }
}
