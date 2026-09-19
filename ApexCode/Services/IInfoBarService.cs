using System;
using System.Threading;
using System.Threading.Tasks;

namespace ApexCode.Services
{
    public enum MistakeLimitDialogResult
    {
        ProvideFeedback,
        Cancel,
        SwitchModel
    }

    public interface IInfoBarService
    {
        Task ShowErrorAsync(string message);
        Task ShowWarningAsync(string message);
        Task ShowInfoAsync(string message);
        
        /// <summary>
        /// Shows a dialog when the agent reaches the consecutive mistake limit.
        /// Allows user to provide feedback, cancel, or switch models.
        /// Cline pattern: index.ts:2257-2283
        /// </summary>
        Task<MistakeLimitDialogResult> ShowMistakeLimitReachedAsync(
            int currentCount, 
            int maxCount, 
            string? customMessage = null,
            CancellationToken ct = default);
    }
}
