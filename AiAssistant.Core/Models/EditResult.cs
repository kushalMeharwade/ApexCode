using System.Collections.Generic;

namespace AiAssistant.Core.Models
{
    public enum EditResultStatus
    {
        Success,
        PartialSuccess,
        FileNotFound,
        MatchNotFound,
        SyntaxError,
        VersionConflict,
        ParseError,
        RetryableError
    }

    public record AppliedEdit(
        string FilePath,
        int StartPosition,
        int Length,
        string OriginalText,
        string ReplacementText
    );

    public record FailedEdit(
        string FilePath,
        string SearchText,
        string Reason,
        double? BestMatchSimilarity
    );

    public class EditResult
    {
        public EditResultStatus Status { get; set; }
        public List<AppliedEdit> AppliedEdits { get; set; } = new();
        public List<FailedEdit> FailedEdits { get; set; } = new();
    }
}
