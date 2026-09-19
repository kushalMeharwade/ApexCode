using System.Collections.Generic;

namespace AiAssistant.Engine.Models;

public class ReplacementValidationResult
{
    public bool Success { get; set; }
    public string NewContent { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public int ExpectedOccurrences { get; set; }
    public int FoundOccurrences { get; set; }
    public List<string> TiersAttempted { get; set; } = new();
    public List<MatchPreview> MatchPreviews { get; set; } = new();
    public ClosestMatchResult? ClosestMatch { get; set; }
    public List<ReplacementSpan> Spans { get; set; } = new();
}

public class BatchEditRequest
{
    public string OldText { get; set; } = string.Empty;
    public string NewText { get; set; } = string.Empty;
    public int ExpectedOccurrences { get; set; } = 1;
}

public class ReplacementSpan
{
    public int StartIndex { get; set; }
    public int Length { get; set; }
    public string NewText { get; set; } = string.Empty;
}

public class MatchPreview
{
    public int Line { get; set; }
    public string Preview { get; set; } = string.Empty;
}

public class ClosestMatchResult
{
    public int Line { get; set; }
    public double Similarity { get; set; }
    public string Preview { get; set; } = string.Empty;
}
