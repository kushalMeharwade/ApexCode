namespace AiAssistant.Engine.Models;

public record EditResult
{
    public bool Success { get; init; }
    public string? FilePath { get; init; }
    public string? OriginalContent { get; init; }
    public string? NewContent { get; init; }
    public string? ErrorMessage { get; init; }
    public int LinesChanged { get; init; } = 0;
}
