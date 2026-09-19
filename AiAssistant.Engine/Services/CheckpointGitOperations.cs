using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Models;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Engine.Services;

public class CheckpointGitOperations
{
    private readonly ILogger _logger;

    public CheckpointGitOperations(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<bool> IsGitAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var (exitCode, _, _) = await RunGitCommandAsync(null, null, new[] { "--version" }, ct);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task InitShadowRepoAsync(string shadowGitDir, string workspacePath, CancellationToken ct = default)
    {
        if (!Directory.Exists(shadowGitDir))
        {
            Directory.CreateDirectory(shadowGitDir);
        }

        // Initialize a bare-like repo but with a worktree
        await RunGitCommandAsync(shadowGitDir, workspacePath, new[] { "init" }, ct);
        await RunGitCommandAsync(shadowGitDir, workspacePath, new[] { "config", "core.worktree", workspacePath }, ct);
        await RunGitCommandAsync(shadowGitDir, workspacePath, new[] { "config", "user.name", "Cline Checkpoint" }, ct);
        await RunGitCommandAsync(shadowGitDir, workspacePath, new[] { "config", "user.email", "checkpoint@cline.bot" }, ct);
        
        EnsureExclusions(shadowGitDir);
    }

    private static string[] ProtectedDirectories(string shadowGitDir)
        => new[] { ".git", ".git_disabled", ".vs", Path.GetFileName(shadowGitDir) }
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static void EnsureExclusions(string shadowGitDir)
    {
        // Applies to existing shadow repositories too. These are local excludes; never
        // modify the user's .gitignore or rely on it to protect IDE state.
        var excludePath = Path.Combine(shadowGitDir, "info", "exclude");
        var excludeDir = Path.GetDirectoryName(excludePath);
        if (excludeDir != null && !Directory.Exists(excludeDir))
        {
            Directory.CreateDirectory(excludeDir);
        }
        var existing = File.Exists(excludePath) ? File.ReadAllText(excludePath) : "";
        var additions = ProtectedDirectories(shadowGitDir).Select(n => n + "/")
            .Where(p => !existing.Split('\n').Any(line => line.TrimEnd('\r') == p)).ToArray();
        if (additions.Length > 0)
            AiAssistant.Storage.SafeFileWriter.WriteAllText(excludePath, existing.TrimEnd() + "\n" + string.Join("\n", additions) + "\n");
    }

    private static string[] WorkspacePathspecs(string shadowGitDir)
        => new[] { "." }.Concat(ProtectedDirectories(shadowGitDir)
            .Select(n => ":(exclude,glob,icase)**/" + n + "/**")).ToArray();

    private async Task UntrackProtectedDirectoriesAsync(string shadowGitDir, string workspacePath, CancellationToken ct)
    {
        var args = new[] { "rm", "-r", "--cached", "--ignore-unmatch", "--" }
            .Concat(ProtectedDirectories(shadowGitDir).Select(n => ":(glob,icase)**/" + n + "/**")).ToArray();
        var (exit, _, error) = await RunGitCommandAsync(shadowGitDir, workspacePath, args, ct);
        if (exit != 0) throw new Exception("Could not exclude IDE state from the checkpoint index: " + error);
    }

    public async Task<string> AddAndCommitAsync(string shadowGitDir, string workspacePath, string message, CancellationToken ct = default)
    {
        EnsureExclusions(shadowGitDir);
        await UntrackProtectedDirectoriesAsync(shadowGitDir, workspacePath, ct);
        var nestedGitDirs = await FindNestedGitDirectoriesAsync(workspacePath, shadowGitDir);
        
        try
        {
            // 1. Rename nested .git to .git_disabled
            await RenameNestedGitDirsAsync(nestedGitDirs, ".git", ".git_disabled");

            // 2. Stage all files (ignore errors from locked files)
            await RunGitCommandAsync(shadowGitDir, workspacePath,
                new[] { "add", "--ignore-errors", "--" }.Concat(WorkspacePathspecs(shadowGitDir)).ToArray(), ct);

            // 3. Commit
            var (exitCode, stdout, stderr) = await RunGitCommandAsync(shadowGitDir, workspacePath, new[] { "commit", "-m", message, "--allow-empty" }, ct);
            if (exitCode != 0 && !stdout.Contains("nothing to commit"))
            {
                throw new Exception($"Failed to commit: {stderr}");
            }

            // 4. Get commit hash
            var (hashExit, hashOut, _) = await RunGitCommandAsync(shadowGitDir, workspacePath, new[] { "rev-parse", "HEAD" }, ct);
            return hashOut.Trim();
        }
        finally
        {
            // Always restore nested .git directories
            await RenameNestedGitDirsAsync(nestedGitDirs, ".git_disabled", ".git");
        }
    }

    public async Task ResetHardAsync(string shadowGitDir, string workspacePath, string commitHash, CancellationToken ct = default)
    {
        EnsureExclusions(shadowGitDir);
        // A hard reset touches IDE files already tracked by older checkpoints even
        // when they are now ignored. Restore only eligible paths instead.
        var (treeExit, tree, treeError) = await RunGitCommandAsync(shadowGitDir, workspacePath,
            new[] { "ls-tree", "-r", "-z", "--name-only", commitHash }, ct);
        if (treeExit != 0) throw new Exception("Could not read checkpoint: " + treeError);
        var pathspecs = WorkspacePathspecs(shadowGitDir);
        var (indexExit, index, indexError) = await RunGitCommandAsync(shadowGitDir, workspacePath,
            new[] { "ls-files", "-z", "--" }.Concat(pathspecs).ToArray(), ct);
        if (indexExit != 0) throw new Exception("Could not read checkpoint index: " + indexError);
        var protectedNames = ProtectedDirectories(shadowGitDir);
        var hasTargetFiles = tree.Split('\0').Any(p => p.Length > 0 &&
            !p.Split('/').Any(part => protectedNames.Contains(part, StringComparer.OrdinalIgnoreCase)));
        if (hasTargetFiles || index.Length > 0)
        {
            var (restoreExit, _, restoreError) = await RunGitCommandAsync(shadowGitDir, workspacePath,
                new[] { "restore", "--source=" + commitHash, "--staged", "--worktree", "--" }.Concat(pathspecs).ToArray(), ct);
            if (restoreExit != 0) throw new Exception("Could not restore checkpoint files: " + restoreError);
        }
        await UntrackProtectedDirectoriesAsync(shadowGitDir, workspacePath, ct);
        
        // Clean untracked files
        var (cleanExit, _, cleanError) = await RunGitCommandAsync(shadowGitDir, workspacePath,
            new[] { "clean", "-fd", "--" }.Concat(pathspecs).ToArray(), ct);
        if (cleanExit != 0)
            throw new Exception($"Files were reset, but files created after the checkpoint could not all be removed: {cleanError}");
    }

    public async Task<IReadOnlyList<CheckpointFileDiff>> GetDiffSummaryAsync(string shadowGitDir, string workspacePath, string fromHash, string? toHash = null, CancellationToken ct = default)
    {
        EnsureExclusions(shadowGitDir);
        var args = new List<string> { "diff", "--numstat", fromHash };
        List<string>? nestedGitDirs = null;

        try
        {
            if (!string.IsNullOrEmpty(toHash))
            {
                args.Add(toHash);
            }
            else
            {
                nestedGitDirs = await FindNestedGitDirectoriesAsync(workspacePath, shadowGitDir);
                await RenameNestedGitDirsAsync(nestedGitDirs, ".git", ".git_disabled");
                
                // Temporarily stage current changes to get a diff against the working tree including untracked files
                await RunGitCommandAsync(shadowGitDir, workspacePath,
                    new[] { "add", "--intent-to-add", "--" }.Concat(WorkspacePathspecs(shadowGitDir)).ToArray(), ct);
            }

            args.Add("--");
            args.AddRange(WorkspacePathspecs(shadowGitDir));

            var (exitCode, stdout, _) = await RunGitCommandAsync(shadowGitDir, workspacePath, args.ToArray(), ct);
            
            var diffs = new List<CheckpointFileDiff>();
            var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var line in lines)
            {
                var parts = line.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    var added = parts[0] == "-" ? 0 : int.TryParse(parts[0], out var a) ? a : 0;
                    var removed = parts[1] == "-" ? 0 : int.TryParse(parts[1], out var r) ? r : 0;
                    var file = parts[2];

                    diffs.Add(new CheckpointFileDiff
                    {
                        FilePath = file,
                        LinesAdded = added,
                        LinesRemoved = removed,
                        ChangeType = added > 0 && removed == 0 ? "added" : removed > 0 && added == 0 ? "deleted" : "modified"
                    });
                }
            }

            return diffs;
        }
        finally
        {
            if (nestedGitDirs != null)
            {
                await RenameNestedGitDirsAsync(nestedGitDirs, ".git_disabled", ".git");
            }
        }
    }

    public async Task<string?> GetFileContentAsync(string shadowGitDir, string workspacePath, string commitHash, string relativeFilePath, CancellationToken ct = default)
    {
        var (exitCode, stdout, stderr) = await RunGitCommandAsync(shadowGitDir, workspacePath, new[] { "show", $"{commitHash}:{relativeFilePath}" }, ct);
        if (exitCode != 0)
        {
            _logger.LogWarning("Failed to get file content for {File} at {Hash}: {Error}", relativeFilePath, commitHash, stderr);
            return null;
        }
        return stdout;
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunGitCommandAsync(string? shadowGitDir, string? workspacePath, string[] args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var escapedArgs = new List<string>();
        if (!string.IsNullOrEmpty(shadowGitDir))
        {
            escapedArgs.Add($"--git-dir=\"{EscapeGitArg(shadowGitDir)}\"");
        }
        if (!string.IsNullOrEmpty(workspacePath))
        {
            psi.WorkingDirectory = workspacePath;
            escapedArgs.Add($"--work-tree=\"{EscapeGitArg(workspacePath)}\"");
        }

        foreach (var arg in args)
        {
            escapedArgs.Add($"\"{EscapeGitArg(arg)}\"");
        }
        psi.Arguments = string.Join(" ", escapedArgs);

        using var process = Process.Start(psi);
        if (process == null) throw new Exception("Failed to start git process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await Task.Run(() => process.WaitForExit(), ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, stdout, stderr);
    }

    private string EscapeGitArg(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "";
        // First, escape any existing double quotes
        arg = arg.Replace("\"", "\\\"");
        // If the argument ends with a backslash, we must escape it so it doesn't escape the closing quote we add later!
        if (arg.EndsWith("\\"))
        {
            arg += "\\";
        }
        return arg;
    }

    private async Task<List<string>> FindNestedGitDirectoriesAsync(string rootPath, string shadowGitDir)
    {
        return await Task.Run(() =>
        {
            var nestedDirs = new List<string>();
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(rootPath, ".git", SearchOption.AllDirectories))
                {
                    // Skip the shadow git dir itself if it happens to be inside the workspace
                    if (string.Equals(dir, shadowGitDir, StringComparison.OrdinalIgnoreCase))
                        continue;

                    nestedDirs.Add(Path.GetDirectoryName(dir)!);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enumerate nested .git directories");
            }
            return nestedDirs;
        });
    }

    private async Task RenameNestedGitDirsAsync(List<string> directories, string fromName, string toName)
    {
        await Task.Run(() =>
        {
            foreach (var dir in directories)
            {
                try
                {
                    var fromPath = Path.Combine(dir, fromName);
                    var toPath = Path.Combine(dir, toName);
                    if (Directory.Exists(fromPath))
                    {
                        Directory.Move(fromPath, toPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to rename {FromName} to {ToName} in {Directory}", fromName, toName, dir);
                }
            }
        });
    }
}
