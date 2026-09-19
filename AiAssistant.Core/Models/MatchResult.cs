namespace AiAssistant.Core.Models
{
    public enum MatchType
    {
        None,
        Exact,
        WhitespaceNormalized,
        LineFuzzy,
        TokenFuzzy
    }

    public class MatchResult
    {
        public MatchType Type { get; set; }
        
        /// <summary>
        /// 1.0 for exact, less for fuzzy matches.
        /// </summary>
        public double ConfidenceScore { get; set; }
        
        public int Position { get; set; }
        
        public int Length { get; set; }
    }
}
