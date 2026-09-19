using System.Threading.Tasks;

namespace AiAssistant.Core.Services;

/// <summary>
/// Logger service that writes directly to the Visual Studio Output Window.
/// </summary>
public interface IOutputLogger
{
    /// <summary>
    /// Logs a message to the "ApexCode" pane in the VS Output Window.
    /// Only logs if EnableLogs is true in the SettingsService.
    /// </summary>
    Task LogAsync(string message);

    /// <summary>
    /// Synchronously logs a message.
    /// </summary>
    void Log(string message);

    /// <summary>
    /// Synchronously logs a message with a specific category and optional detail.
    /// </summary>
    void Log(LogCategory category, string message, string? detail = null);

    /// <summary>
    /// Asynchronously logs a message with a specific category and optional detail.
    /// </summary>
    Task LogAsync(LogCategory category, string message, string? detail = null);
}
