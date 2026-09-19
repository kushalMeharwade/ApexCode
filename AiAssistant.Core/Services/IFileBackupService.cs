using System.Threading.Tasks;

namespace AiAssistant.Core.Services;

public interface IFileBackupService
{
    /// <summary>
    /// Backs up a file before modification.
    /// </summary>
    Task BackupAsync(string filePath, string solutionName);

    /// <summary>
    /// Reverts the most recent file modification.
    /// Returns true if a backup was restored, false if the stack is empty.
    /// </summary>
    Task<bool> UndoLastAsync();

    /// <summary>
    /// Gets a value indicating whether there is an action that can be undone.
    /// </summary>
    bool CanUndo { get; }

    /// <summary>
    /// Fired when the CanUndo property changes.
    /// </summary>
    event EventHandler CanUndoChanged;
}
