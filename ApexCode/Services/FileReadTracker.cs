using System;
using System.Collections.Concurrent;
using AiAssistant.Core.Services;

namespace ApexCode.Services;

public class FileReadTracker : IFileReadTracker
{
    private readonly ConcurrentDictionary<string, FileReadInfo> _lastReadTimes = new(StringComparer.OrdinalIgnoreCase);

    public void MarkFileRead(string filePath, DateTime timestamp, int? startLine = null, int? endLine = null)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        _lastReadTimes[filePath] = new FileReadInfo 
        { 
            Timestamp = timestamp, 
            StartLine = startLine, 
            EndLine = endLine 
        };
    }

    public FileReadInfo? GetLastReadInfo(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;
        if (_lastReadTimes.TryGetValue(filePath, out var info))
        {
            return info;
        }
        return null;
    }
}
