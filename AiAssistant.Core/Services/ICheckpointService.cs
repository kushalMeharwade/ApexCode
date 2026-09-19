using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Models;

namespace AiAssistant.Core.Services;

public interface ICheckpointService
{
    /// <summary>
    /// Whether the checkpoint system is available (git found, valid workspace, etc.)
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Error message if the checkpoint system failed to initialize.
    /// </summary>
    string? InitializationError { get; }

    /// <summary>
    /// Create a checkpoint of the current workspace state.
    /// </summary>
    Task<string?> SaveCheckpointAsync(string sessionId, string messageId, string description, CancellationToken ct = default);

    /// <summary>
    /// Restore workspace to a previous checkpoint.
    /// restoreType: "taskAndWorkspace", "task", or "workspace"
    /// </summary>
    Task<(bool Success, string ErrorMessage)> RestoreCheckpointAsync(string checkpointId, string restoreType, CancellationToken ct = default);

    /// <summary>
    /// Get diff summary between two checkpoints (or current state vs a checkpoint).
    /// </summary>
    Task<IReadOnlyList<CheckpointFileDiff>> GetDiffAsync(string fromCommitHash, string? toCommitHash = null, CancellationToken ct = default);

    /// <summary>
    /// Get all checkpoints for a session.
    /// </summary>
    Task<IReadOnlyList<CheckpointInfo>> GetCheckpointsForSessionAsync(string sessionId);

    /// <summary>
    /// Initialize the checkpoint tracker for the given workspace path.
    /// </summary>
    Task InitializeAsync(string workspacePath, CancellationToken ct = default);

    /// <summary>
    Task<string?> GetFileContentAsync(string commitHash, string relativeFilePath, CancellationToken ct = default);

    /// <summary>
    /// Releases any locks associated with the given session ID.
    /// </summary>
    Task ReleaseLocksAsync(string sessionId, CancellationToken ct = default);
}
