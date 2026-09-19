namespace AiAssistant.Core.Models;

public record CheckpointFileDiff
{
    public string FilePath { get; init; } = string.Empty;
    public string ChangeType { get; init; } = string.Empty; // "added", "modified", "deleted", "renamed"
    public int LinesAdded { get; init; }
    public int LinesRemoved { get; init; }
}
