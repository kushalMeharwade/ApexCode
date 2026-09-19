using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Engine.Services;

public class CheckpointTracker
{
    private readonly string _workspacePath;
    private readonly string _shadowGitDir;
    private readonly CheckpointGitOperations _git;
    private readonly ILogger _logger;

    public string WorkspacePath => _workspacePath;

    public CheckpointTracker(string workspacePath, CheckpointGitOperations git, ILogger logger)
    {
        _workspacePath = workspacePath;
        _git = git;
        _logger = logger;

        // SHA-256 instead of GetHashCode() to eliminate the ~1-in-65536 collision risk
        // where two different workspace paths map to the same shadow git directory on a
        // shared machine, silently cross-contaminating checkpoint histories.
        // We take the first 16 hex chars of the SHA-256 of the lowercase normalised path
        // — 64 bits of entropy, which is more than sufficient for a local discriminator.
        using var sha = System.Security.Cryptography.SHA256.Create();
        var cwdHash = BitConverter.ToString(
            sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(workspacePath.ToLowerInvariant())))
            .Replace("-", "")
            .Substring(0, 16);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _shadowGitDir = Path.Combine(localAppData, "AiAssistant", "Checkpoints", cwdHash, ".git");
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _git.InitShadowRepoAsync(_shadowGitDir, _workspacePath, ct);
    }

    public async Task<string> CommitAsync(string message, CancellationToken ct = default)
    {
        var lockDir = Path.GetDirectoryName(_shadowGitDir)!;
        using var lockObj = await CheckpointLock.AcquireAsync(lockDir, TimeSpan.FromSeconds(30));
        return await _git.AddAndCommitAsync(_shadowGitDir, _workspacePath, message, ct);
    }

    public async Task ResetAsync(string commitHash, CancellationToken ct = default)
    {
        var lockDir = Path.GetDirectoryName(_shadowGitDir)!;
        using var lockObj = await CheckpointLock.AcquireAsync(lockDir, TimeSpan.FromSeconds(30));
        await _git.ResetHardAsync(_shadowGitDir, _workspacePath, commitHash, ct);
    }

    public async Task<IReadOnlyList<CheckpointFileDiff>> GetDiffSetAsync(string fromHash, string? toHash = null, CancellationToken ct = default)
    {
        return await _git.GetDiffSummaryAsync(_shadowGitDir, _workspacePath, fromHash, toHash, ct);
    }
}
