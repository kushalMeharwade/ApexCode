using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using AiAssistant.Core.Services;
using System.Linq;
using System.Threading.Tasks;

namespace AiAssistant.Engine.Services;

public class TransactionSnapshotService : ITransactionSnapshotService
{
    public string BeginTransaction(string workspaceRoot, Dictionary<string, string> originalContents)
    {
        // Validate workspaceRoot is absolute and safe
        if (!Path.IsPathRooted(workspaceRoot))
        {
            throw new ArgumentException("Workspace root must be an absolute path.", nameof(workspaceRoot));
        }

        var transactionId = Guid.NewGuid().ToString("N");
        var transactionDir = Path.Combine(workspaceRoot, ".vs", "agent_transactions", transactionId);
        
        // Validate transaction directory is within workspace
        var normalizedWorkspace = Path.GetFullPath(workspaceRoot);
        var normalizedTransactionDir = Path.GetFullPath(transactionDir);
        if (!normalizedTransactionDir.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Transaction directory must be within workspace root.");
        }

        Directory.CreateDirectory(transactionDir);

        var manifest = new List<object>();
        int fileIndex = 0;

        foreach (var kvp in originalContents)
        {
            var filePath = kvp.Key;
            var content = kvp.Value;
            
            // Validate file path is within workspace
            var normalizedFilePath = Path.GetFullPath(filePath);
            if (!normalizedFilePath.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"File path '{filePath}' is outside workspace root.");
            }

            var snapshotFileName = $"file_{fileIndex}.txt";
            var snapshotPath = Path.Combine(transactionDir, snapshotFileName);
            
            File.WriteAllText(snapshotPath, content);
            
            // Enhanced manifest with metadata
            manifest.Add(new
            {
                originalPath = filePath,
                snapshotFile = snapshotFileName,
                timestamp = DateTime.UtcNow,
                contentHash = ComputeSimpleHash(content)
            });
            fileIndex++;
        }

        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(transactionDir, "manifest.json"), manifestJson);

        return transactionId;
    }

    private static string ComputeSimpleHash(string content)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }

    public string BeginTransaction(string workspaceRoot, IEnumerable<string> filePaths)
    {
        // Validate workspaceRoot is absolute and safe
        if (!Path.IsPathRooted(workspaceRoot))
        {
            throw new ArgumentException("Workspace root must be an absolute path.", nameof(workspaceRoot));
        }

        var transactionId = Guid.NewGuid().ToString("N");
        var transactionDir = Path.Combine(workspaceRoot, ".vs", "agent_transactions", transactionId);
        
        // Validate transaction directory is within workspace
        var normalizedWorkspace = Path.GetFullPath(workspaceRoot);
        var normalizedTransactionDir = Path.GetFullPath(transactionDir);
        if (!normalizedTransactionDir.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Transaction directory must be within workspace root.");
        }

        Directory.CreateDirectory(transactionDir);

        var manifest = new List<object>();
        int fileIndex = 0;

        foreach (var filePath in filePaths)
        {
            if (File.Exists(filePath))
            {
                // Validate file path is within workspace
                var normalizedFilePath = Path.GetFullPath(filePath);
                if (!normalizedFilePath.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"File path '{filePath}' is outside workspace root.");
                }

                var snapshotFileName = $"file_{fileIndex}.txt";
                var snapshotPath = Path.Combine(transactionDir, snapshotFileName);
                
                // Capture file content at snapshot time, not just copy
                var content = File.ReadAllText(filePath);
                File.WriteAllText(snapshotPath, content);
                
                manifest.Add(new
                {
                    originalPath = filePath,
                    snapshotFile = snapshotFileName,
                    timestamp = DateTime.UtcNow,
                    contentHash = ComputeSimpleHash(content)
                });
                fileIndex++;
            }
        }

        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(transactionDir, "manifest.json"), manifestJson);

        return transactionId;
    }

    public string BeginTransaction(string workspaceRoot, Dictionary<string, string> inMemoryContents, IEnumerable<string> onDiskFiles)
    {
        // Validate workspaceRoot is absolute and safe
        if (!Path.IsPathRooted(workspaceRoot))
        {
            throw new ArgumentException("Workspace root must be an absolute path.", nameof(workspaceRoot));
        }

        var transactionId = Guid.NewGuid().ToString("N");
        var transactionDir = Path.Combine(workspaceRoot, ".vs", "agent_transactions", transactionId);
        
        // Validate transaction directory is within workspace
        var normalizedWorkspace = Path.GetFullPath(workspaceRoot);
        var normalizedTransactionDir = Path.GetFullPath(transactionDir);
        if (!normalizedTransactionDir.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Transaction directory must be within workspace root.");
        }

        Directory.CreateDirectory(transactionDir);

        var manifest = new List<object>();
        int fileIndex = 0;

        foreach (var kvp in inMemoryContents)
        {
            var filePath = kvp.Key;
            var content = kvp.Value;
            
            // Validate file path is within workspace
            var normalizedFilePath = Path.GetFullPath(filePath);
            if (!normalizedFilePath.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"File path '{filePath}' is outside workspace root.");
            }

            var snapshotFileName = $"file_{fileIndex}.txt";
            var snapshotPath = Path.Combine(transactionDir, snapshotFileName);
            
            File.WriteAllText(snapshotPath, content);
            manifest.Add(new
            {
                originalPath = filePath,
                snapshotFile = snapshotFileName,
                timestamp = DateTime.UtcNow,
                contentHash = ComputeSimpleHash(content)
            });
            fileIndex++;
        }

        foreach (var filePath in onDiskFiles)
        {
            if (File.Exists(filePath))
            {
                // Validate file path is within workspace
                var normalizedFilePath = Path.GetFullPath(filePath);
                if (!normalizedFilePath.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"File path '{filePath}' is outside workspace root.");
                }

                var snapshotFileName = $"file_{fileIndex}.txt";
                var snapshotPath = Path.Combine(transactionDir, snapshotFileName);
                
                // Capture file content at snapshot time
                var content = File.ReadAllText(filePath);
                File.WriteAllText(snapshotPath, content);
                
                manifest.Add(new
                {
                    originalPath = filePath,
                    snapshotFile = snapshotFileName,
                    timestamp = DateTime.UtcNow,
                    contentHash = ComputeSimpleHash(content)
                });
                fileIndex++;
            }
        }

        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(transactionDir, "manifest.json"), manifestJson);

        return transactionId;
    }


    public void CompleteTransaction(string workspaceRoot, string transactionId)
    {
        var transactionDir = Path.Combine(workspaceRoot, ".vs", "agent_transactions", transactionId);
        if (Directory.Exists(transactionDir))
        {
            try
            {
                Directory.Delete(transactionDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    public bool HasOrphanedTransactions(string workspaceRoot)
    {
        var transactionsDir = Path.Combine(workspaceRoot, ".vs", "agent_transactions");
        if (!Directory.Exists(transactionsDir)) return false;

        return Directory.GetDirectories(transactionsDir).Length > 0;
    }

    public void CleanupOrphanedTransactions(string workspaceRoot)
    {
        var transactionsDir = Path.Combine(workspaceRoot, ".vs", "agent_transactions");
        if (Directory.Exists(transactionsDir))
        {
            try
            {
                Directory.Delete(transactionsDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    public async Task RevertOrphanedTransactionsAsync(string workspaceRoot)
    {
        var transactionsDir = Path.Combine(workspaceRoot, ".vs", "agent_transactions");
        if (!Directory.Exists(transactionsDir)) return;

        foreach (var transactionDir in Directory.GetDirectories(transactionsDir))
        {
            var manifestPath = Path.Combine(transactionDir, "manifest.json");
            if (File.Exists(manifestPath))
            {
                try
                {
                    var manifestJson = File.ReadAllText(manifestPath);
                    using var document = JsonDocument.Parse(manifestJson);
                    foreach (var item in document.RootElement.EnumerateArray())
                    {
                        var originalPath = item.GetProperty("originalPath").GetString();
                        var snapshotFile = item.GetProperty("snapshotFile").GetString();

                        if (!string.IsNullOrEmpty(originalPath) && !string.IsNullOrEmpty(snapshotFile))
                        {
                            var snapshotPath = Path.Combine(transactionDir, snapshotFile);
                            if (File.Exists(snapshotPath))
                            {
                                var originalContent = File.ReadAllText(snapshotPath);
                                File.WriteAllText(originalPath, originalContent);
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore parsing or copying errors on orphaned revert
                }
            }
        }

        CleanupOrphanedTransactions(workspaceRoot);
        await Task.CompletedTask;
    }

    public async Task RevertTransactionAsync(string workspaceRoot, string transactionId)
    {
        var transactionDir = Path.Combine(workspaceRoot, ".vs", "agent_transactions", transactionId);
        if (!Directory.Exists(transactionDir)) return;

        var manifestPath = Path.Combine(transactionDir, "manifest.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                var manifestJson = File.ReadAllText(manifestPath);
                using var document = JsonDocument.Parse(manifestJson);
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    var originalPath = item.GetProperty("originalPath").GetString();
                    var snapshotFile = item.GetProperty("snapshotFile").GetString();

                    if (!string.IsNullOrEmpty(originalPath) && !string.IsNullOrEmpty(snapshotFile))
                    {
                        var snapshotPath = Path.Combine(transactionDir, snapshotFile);
                        if (File.Exists(snapshotPath))
                        {
                            var originalContent = File.ReadAllText(snapshotPath);
                            File.WriteAllText(originalPath, originalContent);
                        }
                    }
                }
            }
            catch
            {
                // Ignore restore errors
            }
        }
        
        CompleteTransaction(workspaceRoot, transactionId);
        await Task.CompletedTask;
    }
}
