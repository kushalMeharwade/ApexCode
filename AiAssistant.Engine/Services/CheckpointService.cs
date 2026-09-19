using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Models;
using AiAssistant.Core.Services;
using AiAssistant.Storage.Models;
using AiAssistant.Storage.Repositories;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Engine.Services;

public class CheckpointService : ICheckpointService
{
    private readonly ICheckpointRepository _repository;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<CheckpointService> _logger;
    private CheckpointTracker? _tracker;
    private bool _isAvailable;
    private string? _initializationError;

    public bool IsAvailable => _isAvailable;
    public string? InitializationError => _initializationError;

    public CheckpointService(ICheckpointRepository repository, ISessionManager sessionManager, ILogger<CheckpointService> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InitializeAsync(string workspacePath, CancellationToken ct = default)
    {
        try
        {
            var git = new CheckpointGitOperations(_logger);
            
            // 1. Check git availability
            if (!await git.IsGitAvailableAsync(ct))
            {
                _isAvailable = false;
                _initializationError = "Git must be installed to use checkpoints.";
                _logger.LogWarning("Git not found. Checkpoints are disabled.");
                return;
            }

            // 2. Validate workspace path (prevent doing this on Desktop/Documents etc.)
            if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            {
                _isAvailable = false;
                _initializationError = "Invalid workspace path.";
                return;
            }
            
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var protectedDirs = new[]
            {
                userProfile,
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Path.Combine(userProfile, "Downloads")
            };
            
            if (protectedDirs.Any(d => string.Equals(Path.GetFullPath(workspacePath).TrimEnd('\\'), d.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            {
                _isAvailable = false;
                _initializationError = "Cannot enable checkpoints on protected system directories.";
                _logger.LogWarning("Workspace is a protected directory. Checkpoints are disabled.");
                return;
            }

            // 3. Initialize Tracker
            _tracker = new CheckpointTracker(workspacePath, git, _logger);
            await _tracker.InitializeAsync(ct);

            _isAvailable = true;
            _initializationError = null;
            _logger.LogInformation("Checkpoint system initialized for {WorkspacePath}", workspacePath);
        }
        catch (Exception ex)
        {
            _isAvailable = false;
            _initializationError = $"Failed to initialize checkpoints: {ex.Message}";
            _logger.LogError(ex, "Failed to initialize CheckpointService.");
        }
    }

    public async Task<string?> SaveCheckpointAsync(string sessionId, string messageId, string description, CancellationToken ct = default)
    {
        if (!_isAvailable || _tracker == null) return null;

        try
        {
            // 1. Commit
            var commitMessage = $"checkpoint-{sessionId}-{messageId}";
            var commitHash = await _tracker.CommitAsync(commitMessage, ct);

            // 2. Save DB record
            var checkpoint = new Checkpoint
            {
                SessionId = sessionId,
                MessageId = messageId,
                CommitHash = commitHash,
                WorkspacePath = _tracker.WorkspacePath,
                Description = description,
                CreatedAt = DateTime.UtcNow
            };
            await _repository.SaveAsync(checkpoint);

            _logger.LogInformation("Created checkpoint {CommitHash} for message {MessageId}", commitHash, messageId);
            return checkpoint.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save checkpoint for message {MessageId}", messageId);
            return null;
        }
    }

    public async Task<(bool Success, string ErrorMessage)> RestoreCheckpointAsync(string checkpointId, string restoreType, CancellationToken ct = default)
    {
        if (restoreType != "task" && restoreType != "workspace" && restoreType != "taskAndWorkspace")
            return (false, "Unknown checkpoint action.");
        if (restoreType != "task" && (!_isAvailable || _tracker == null))
            return (false, "Checkpoint system is not available.");

        bool filesRestored = false;
        try
        {
            var checkpoint = await _repository.GetByIdAsync(checkpointId);
            if (checkpoint == null)
            {
                _logger.LogWarning("Checkpoint {CheckpointId} not found in DB.", checkpointId);
                return (false, "Checkpoint not found in database.");
            }

            // Validate the conversation boundary before changing files. Older versions used
            // unrelated message IDs; never guess which messages to delete for those checkpoints.
            if (restoreType != "workspace")
            {
                var history = await _sessionManager.GetConversationHistoryAsync(checkpoint.SessionId);
                if (!history.Any(m => m.Id == checkpoint.MessageId && m.Role == "user"))
                    return (false, "This checkpoint cannot be matched to a saved request. You can still use Restore files only.");
                if (checkpoint.Description != "Before AI Turn")
                    return (false, "Choose a checkpoint from before the request to revert the chat.");
            }

            ct.ThrowIfCancellationRequested();
            // If restoring workspace, do the git reset
            if (restoreType == "workspace" || restoreType == "taskAndWorkspace")
            {
                // Ensure the tracker matches the checkpoint workspace
                if (!string.Equals(_tracker!.WorkspacePath, checkpoint.WorkspacePath, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Workspace path mismatch. Current: {Current}, Checkpoint: {Checkpoint}", _tracker.WorkspacePath, checkpoint.WorkspacePath);
                    return (false, "Workspace path mismatch.");
                }

                await _tracker.ResetAsync(checkpoint.CommitHash, ct);
                filesRestored = true;
                _logger.LogInformation("Restored workspace to checkpoint {CommitHash}", checkpoint.CommitHash);
            }

            if (restoreType != "workspace")
                await _sessionManager.RevertMessagesAsync(checkpoint.SessionId, checkpoint.MessageId);

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore checkpoint {CheckpointId}", checkpointId);
            return (false, filesRestored
                ? "Files were restored, but the conversation could not be reverted. " + ex.Message
                : ex.Message);
        }
    }

    public async Task<IReadOnlyList<CheckpointFileDiff>> GetDiffAsync(string fromCommitHash, string? toCommitHash = null, CancellationToken ct = default)
    {
        if (!_isAvailable || _tracker == null) return Array.Empty<CheckpointFileDiff>();
        try
        {
            return await _tracker.GetDiffSetAsync(fromCommitHash, toCommitHash, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get diff from {From} to {To}", fromCommitHash, toCommitHash);
            return Array.Empty<CheckpointFileDiff>();
        }
    }

    public async Task<IReadOnlyList<CheckpointInfo>> GetCheckpointsForSessionAsync(string sessionId)
    {
        var records = await _repository.GetBySessionIdAsync(sessionId);
        var result = new List<CheckpointInfo>();
        foreach(var r in records)
        {
            result.Add(new CheckpointInfo
            {
                Id = r.Id,
                SessionId = r.SessionId,
                MessageId = r.MessageId,
                CommitHash = r.CommitHash,
                Description = r.Description,
                CreatedAt = r.CreatedAt
            });
        }
        return result;
    }

    public async Task<string?> GetFileContentAsync(string commitHash, string relativeFilePath, CancellationToken ct = default)
    {
        if (!_isAvailable || _tracker == null) return null;
        try
        {
            // The file path must use forward slashes for git
            var normalizedPath = relativeFilePath.Replace("\\", "/");
            var git = new CheckpointGitOperations(_logger);
            
            var cwdHash = _tracker.WorkspacePath.GetHashCode().ToString("x8");
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var shadowGitDir = Path.Combine(localAppData, "AiAssistant", "Checkpoints", cwdHash, ".git");

            return await git.GetFileContentAsync(shadowGitDir, _tracker.WorkspacePath, commitHash, normalizedPath, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get file content from checkpoint");
            return null;
        }
    }

    public Task ReleaseLocksAsync(string sessionId, CancellationToken ct = default)
    {
        // No file or process locks are held by CheckpointService; checkpoints use a
        // shadow git repo and are committed atomically. Nothing to release.
        _logger.LogDebug("ReleaseLocksAsync called for session {SessionId} – no-op", sessionId);
        return Task.CompletedTask;
    }
}
