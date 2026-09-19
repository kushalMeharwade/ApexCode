namespace AiAssistant.Engine.Models;

/// <summary>
/// Represents a diff preview with structured change information.
/// </summary>
public record DiffPreviewResult
{
    public string FilePath { get; init; } = string.Empty;
    public string? OriginalContent { get; init; }
    public string? NewContent { get; init; }
    public bool IsValid { get; init; } = true;
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Raw diff lines in unified-diff-like format (-, +, space prefix).
    /// </summary>
    public IEnumerable<string> Changes { get; init; } = Enumerable.Empty<string>();

    /// <summary>
    /// Structured change blocks with line numbers.
    /// </summary>
    public IReadOnlyList<DiffBlock> Blocks { get; init; } = Array.Empty<DiffBlock>();

    /// <summary>
    /// Total lines added.
    /// </summary>
    public int LinesAdded { get; init; }

    /// <summary>
    /// Total lines removed.
    /// </summary>
    public int LinesRemoved { get; init; }

    /// <summary>
    /// Total lines modified (counted as both added and removed).
    /// </summary>
    public int LinesModified { get; init; }

    /// <summary>
    /// Total unchanged lines.
    /// </summary>
    public int LinesUnchanged { get; init; }
}

/// <summary>
/// A single block of changes in a diff.
/// </summary>
public record DiffBlock
{
    /// <summary>
    /// Starting line number in the original file (1-based).
    /// </summary>
    public int OriginalStartLine { get; init; }

    /// <summary>
    /// Number of lines from the original file in this block.
    /// </summary>
    public int OriginalLineCount { get; init; }

    /// <summary>
    /// Starting line number in the new file (1-based).
    /// </summary>
    public int NewStartLine { get; init; }

    /// <summary>
    /// Number of lines from the new file in this block.
    /// </summary>
    public int NewLineCount { get; init; }

    /// <summary>
    /// Lines in this block: removed (-), added (+), or unchanged (space).
    /// </summary>
    public IReadOnlyList<DiffLine> Lines { get; init; } = Array.Empty<DiffLine>();
}

/// <summary>
/// A single line in a diff block.
/// </summary>
public record DiffLine
{
    public DiffLineType Type { get; init; }
    public string Content { get; init; } = "";
    public int? OriginalLineNumber { get; init; }
    public int? NewLineNumber { get; init; }
}

public enum DiffLineType
{
    Unchanged,
    Added,
    Removed
}
