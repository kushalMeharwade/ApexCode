using System;
using System.IO;
using System.Threading.Tasks;

namespace AiAssistant.Engine.Services;

public sealed class CheckpointLock : IDisposable
{
    private readonly FileStream _lockFile;
    private readonly string _lockFilePath;

    private CheckpointLock(FileStream lockFile, string lockFilePath)
    {
        _lockFile = lockFile;
        _lockFilePath = lockFilePath;
    }

    public static async Task<CheckpointLock> AcquireAsync(string lockDir, TimeSpan timeout)
    {
        if (!Directory.Exists(lockDir))
        {
            Directory.CreateDirectory(lockDir);
        }

        var lockFilePath = Path.Combine(lockDir, "checkpoint.lock");
        var startTime = DateTime.UtcNow;

        while (DateTime.UtcNow - startTime < timeout)
        {
            try
            {
                var stream = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
                return new CheckpointLock(stream, lockFilePath);
            }
            catch (IOException)
            {
                // Lock is held by another process/thread
                await Task.Delay(100);
            }
        }

        throw new TimeoutException($"Failed to acquire checkpoint lock at {lockFilePath} within {timeout.TotalSeconds} seconds.");
    }

    public void Dispose()
    {
        try
        {
            _lockFile.Dispose();
        }
        catch
        {
            // Ignore errors during disposal
        }
    }
}
