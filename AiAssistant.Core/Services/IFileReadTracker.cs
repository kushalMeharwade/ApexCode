using System;

namespace AiAssistant.Core.Services;

public class FileReadInfo
{
    public DateTime Timestamp { get; set; }
    public int? StartLine { get; set; }
    public int? EndLine { get; set; }
}

public interface IFileReadTracker
{
    /// <summary>
    /// Marks that a file was read by the LLM at the specified time.
    /// </summary>
    void MarkFileRead(string filePath, DateTime timestamp, int? startLine = null, int? endLine = null);

    /// <summary>
    /// Retrieves the info when the file was last read by the LLM.
    /// Returns null if the file hasn't been read in the current session.
    /// </summary>
    FileReadInfo? GetLastReadInfo(string filePath);
}
