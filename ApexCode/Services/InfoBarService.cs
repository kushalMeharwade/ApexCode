using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Imaging;

namespace ApexCode.Services
{
    public class InfoBarService : IInfoBarService
    {
        private readonly IServiceProvider _serviceProvider;

        public InfoBarService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async Task ShowErrorAsync(string message)
        {
            await ShowInfoBarAsync(message, KnownMonikers.StatusError);
        }

        public async Task ShowWarningAsync(string message)
        {
            await ShowInfoBarAsync(message, KnownMonikers.StatusWarning);
        }

        public async Task ShowInfoAsync(string message)
        {
            await ShowInfoBarAsync(message, KnownMonikers.StatusInformation);
        }

        public async Task<MistakeLimitDialogResult> ShowMistakeLimitReachedAsync(
            int currentCount, 
            int maxCount, 
            string? customMessage = null,
            CancellationToken ct = default)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var message = customMessage ?? 
                $"The AI has made {currentCount} consecutive mistakes without successfully using tools.\n\n" +
                "This may indicate:\n" +
                "• The task is too complex for the current model\n" +
                "• The model needs clarification or guidance\n" +
                "• A different approach is needed\n\n" +
                "Would you like to provide feedback to help the AI continue?";

            // Use VS MessageBox for now (can be upgraded to custom dialog later)
            var result = VsShellUtilities.ShowMessageBox(
                _serviceProvider,
                message,
                "AI Agent Needs Guidance",
                OLEMSGICON.OLEMSGICON_WARNING,
                OLEMSGBUTTON.OLEMSGBUTTON_ABORTRETRYIGNORE,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

            return result switch
            {
                1 => MistakeLimitDialogResult.ProvideFeedback,  // OK/Retry
                2 => MistakeLimitDialogResult.Cancel,           // Cancel
                3 => MistakeLimitDialogResult.SwitchModel,      // Ignore (repurposed as Switch)
                _ => MistakeLimitDialogResult.Cancel
            };
        }

        private async Task ShowInfoBarAsync(string message, Microsoft.VisualStudio.Imaging.Interop.ImageMoniker icon)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var factory = (IVsInfoBarUIFactory)_serviceProvider.GetService(typeof(SVsInfoBarUIFactory));
            var shell = (IVsShell)_serviceProvider.GetService(typeof(SVsShell));

            if (factory == null || shell == null) return;

            var textSpan = new InfoBarTextSpan(message);
            var infoBarModel = new InfoBarModel(new[] { textSpan }, icon, isCloseButtonVisible: true);
            var uiElement = factory.CreateInfoBar(infoBarModel);

            const int VSPROPID_MainWindowInfoBarHost = -9053;
            shell.GetProperty(VSPROPID_MainWindowInfoBarHost, out var hostObj);
            if (hostObj is IVsInfoBarHost host)
            {
                host.AddInfoBar(uiElement);
            }
        }
    }
}
