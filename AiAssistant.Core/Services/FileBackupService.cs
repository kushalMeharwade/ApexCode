using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Core.Services;

public class FileBackupService : IFileBackupService
{
    private readonly ILogger<FileBackupService> _logger;
    private readonly Stack<string> _backupStack = new Stack<string>(20); // Max 20 backups

    public event EventHandler? CanUndoChanged;

    public FileBackupService(ILogger<FileBackupService> logger)
    {
        _logger = logger;
    }

    public bool CanUndo => _backupStack.Count > 0;

    public async Task BackupAsync(string filePath, string solutionName)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            _logger.LogWarning("Cannot backup file that does not exist: {FilePath}", filePath);
            return;
        }

        try
        {
            solutionName = string.IsNullOrEmpty(solutionName) ? "UnknownSolution" : solutionName;
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var backupDir = Path.Combine(localAppData, "AiAssistant", "Backups", solutionName);

            if (!Directory.Exists(backupDir))
            {
                Directory.CreateDirectory(backupDir);
            }

            var fileName = Path.GetFileName(filePath);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var backupFileName = $"{fileName}.{timestamp}.bak";
            var backupFilePath = Path.Combine(backupDir, backupFileName);

            using (var sourceStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var destStream = new FileStream(backupFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await sourceStream.CopyToAsync(destStream);
            }

            // We store the mapping of the original file path and the backup file path
            var stackEntry = $"{filePath}|{backupFilePath}";
            
            // Limit stack to 20
            if (_backupStack.Count >= 20)
            {
                // To remove bottom of stack, we'd need a different structure, but Stack is simple.
                // We'll just reverse, skip 1, and re-stack, though inefficient, it's small.
                var temp = new List<string>(_backupStack);
                temp.RemoveAt(temp.Count - 1); // removes oldest
                _backupStack.Clear();
                for (int i = temp.Count - 1; i >= 0; i--)
                    _backupStack.Push(temp[i]);
            }

            _backupStack.Push(stackEntry);
            _logger.LogInformation("Created backup of {FilePath} to {BackupFilePath}", filePath, backupFilePath);

            CanUndoChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to backup file {FilePath}", filePath);
        }
    }

    public async Task<bool> UndoLastAsync()
    {
        if (_backupStack.Count == 0) return false;

        var stackEntry = _backupStack.Pop();
        var parts = stackEntry.Split('|');
        if (parts.Length != 2) return false;

        var originalFilePath = parts[0];
        var backupFilePath = parts[1];

        if (!File.Exists(backupFilePath))
        {
            _logger.LogWarning("Backup file not found: {BackupFilePath}", backupFilePath);
            CanUndoChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }

        try
        {
            using (var sourceStream = new FileStream(backupFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var destStream = new FileStream(originalFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await sourceStream.CopyToAsync(destStream);
            }

            _logger.LogInformation("Restored {OriginalFilePath} from {BackupFilePath}", originalFilePath, backupFilePath);
            CanUndoChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore file {OriginalFilePath} from {BackupFilePath}", originalFilePath, backupFilePath);
            // Push it back since it failed? Let's assume we can't restore it so we discard it.
            CanUndoChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }
    }
}
