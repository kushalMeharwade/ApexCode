using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using AiAssistant.Core.Models;

namespace AiAssistant.Engine.Services
{
    public class FuzzyMatchFinder
    {
        public MatchResult FindMatch(string source, string searchText)
        {
            if (string.IsNullOrEmpty(searchText))
            {
                return new MatchResult { Type = MatchType.None, ConfidenceScore = 0 };
            }

            // Layer 1: Exact Match
            var exactMatch = FindExactMatch(source, searchText);
            if (exactMatch != null) return exactMatch;

            // Layer 2: Whitespace Normalized Match
            var wsMatch = FindWhitespaceNormalizedMatch(source, searchText);
            if (wsMatch != null) return wsMatch;

            // Layer 3: Line Fuzzy Match
            var lineMatch = FindLineFuzzyMatch(source, searchText);
            if (lineMatch != null) return lineMatch;

            // Log failure metrics as requested
            Trace.WriteLine($"[METRICS] MATCH_FAILURE: Failed to find search text. Length: {searchText.Length}");

            return new MatchResult { Type = MatchType.None, ConfidenceScore = 0 };
        }

        private MatchResult? FindExactMatch(string source, string search)
        {
            int index = source.IndexOf(search, StringComparison.Ordinal);
            if (index >= 0)
            {
                return new MatchResult
                {
                    Type = MatchType.Exact,
                    ConfidenceScore = 1.0,
                    Position = index,
                    Length = search.Length
                };
            }

            // Fallback: LF normalized exact match
            string normSource = source.Replace("\r\n", "\n");
            string normSearch = search.Replace("\r\n", "\n");
            index = normSource.IndexOf(normSearch, StringComparison.Ordinal);
            if (index >= 0)
            {
                // We must map the normalized index back to the original string.
                // An easy way is to count how many \r were stripped before the index.
                int origPos = MapIndexFromNormalized(source, index);
                int origLen = MapIndexFromNormalized(source, index + normSearch.Length) - origPos;

                return new MatchResult
                {
                    Type = MatchType.Exact, // Close enough to Exact
                    ConfidenceScore = 0.99,
                    Position = origPos,
                    Length = origLen
                };
            }

            return null;
        }

        private int MapIndexFromNormalized(string original, int normIndex)
        {
            int mapped = 0;
            int norm = 0;
            while (norm < normIndex && mapped < original.Length)
            {
                if (original[mapped] == '\r')
                {
                    mapped++; // Skip \r in original since it wasn't in normalized count
                    continue;
                }
                mapped++;
                norm++;
            }
            return mapped;
        }

        private MatchResult? FindWhitespaceNormalizedMatch(string source, string search)
        {
            var sourceTokens = TokenizeWhitespace(source);
            var searchTokens = TokenizeWhitespace(search);
            
            string sourceNorm = string.Join("", sourceTokens.Select(t => t.IsWhitespace ? " " : t.Text));
            string searchNorm = string.Join("", searchTokens.Select(t => t.IsWhitespace ? " " : t.Text));

            int index = sourceNorm.IndexOf(searchNorm, StringComparison.Ordinal);
            if (index >= 0)
            {
                int startPos = MapNormalizedToOriginal(sourceTokens, index);
                int endPos = MapNormalizedToOriginal(sourceTokens, index + searchNorm.Length);

                return new MatchResult
                {
                    Type = MatchType.WhitespaceNormalized,
                    ConfidenceScore = 0.95,
                    Position = startPos,
                    Length = endPos - startPos
                };
            }
            return null;
        }

        private int MapNormalizedToOriginal(List<WsToken> tokens, int normIndex)
        {
            int currentNorm = 0;
            int currentOrig = 0;

            foreach (var token in tokens)
            {
                int tokenNormLen = token.IsWhitespace ? 1 : token.Text.Length;
                
                if (currentNorm + tokenNormLen > normIndex)
                {
                    int offset = normIndex - currentNorm;
                    if (token.IsWhitespace)
                    {
                        return token.OriginalStart;
                    }
                    return token.OriginalStart + offset;
                }
                currentNorm += tokenNormLen;
                currentOrig += token.OriginalLength;
            }
            return currentOrig;
        }

        private List<WsToken> TokenizeWhitespace(string text)
        {
            var tokens = new List<WsToken>();
            int i = 0;
            while (i < text.Length)
            {
                bool isWs = char.IsWhiteSpace(text[i]);
                int start = i;
                while (i < text.Length && char.IsWhiteSpace(text[i]) == isWs)
                {
                    i++;
                }
                tokens.Add(new WsToken
                {
                    IsWhitespace = isWs,
                    OriginalStart = start,
                    OriginalLength = i - start,
                    Text = text.Substring(start, i - start)
                });
            }
            return tokens;
        }

        private class WsToken
        {
            public bool IsWhitespace { get; set; }
            public int OriginalStart { get; set; }
            public int OriginalLength { get; set; }
            public string Text { get; set; }
        }

        private MatchResult? FindLineFuzzyMatch(string source, string search)
        {
            var sourceLines = GetLines(source);
            var searchLines = GetLines(search).Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();

            if (searchLines.Count == 0) return null;

            for (int i = 0; i < sourceLines.Count; i++)
            {
                if (MatchLines(sourceLines, i, searchLines))
                {
                    int startPos = sourceLines[i].Start;
                    // Find the last matched source line
                    int lastMatchIdx = i;
                    int searchIdx = 0;
                    while (lastMatchIdx < sourceLines.Count && searchIdx < searchLines.Count)
                    {
                        if (!string.IsNullOrWhiteSpace(sourceLines[lastMatchIdx].Text))
                        {
                            searchIdx++;
                        }
                        if (searchIdx == searchLines.Count) break;
                        lastMatchIdx++;
                    }

                    int endPos = sourceLines[lastMatchIdx].Start + sourceLines[lastMatchIdx].Length;
                    return new MatchResult
                    {
                        Type = MatchType.LineFuzzy,
                        ConfidenceScore = 0.90,
                        Position = startPos,
                        Length = endPos - startPos
                    };
                }
            }
            return null;
        }

        private bool MatchLines(List<LineInfo> sourceLines, int startSource, List<LineInfo> searchLines)
        {
            int sourceIdx = startSource;
            int searchIdx = 0;

            while (searchIdx < searchLines.Count && sourceIdx < sourceLines.Count)
            {
                var sourceLine = sourceLines[sourceIdx];
                if (string.IsNullOrWhiteSpace(sourceLine.Text))
                {
                    sourceIdx++;
                    continue;
                }

                var searchLine = searchLines[searchIdx];
                if (sourceLine.Text.Trim() != searchLine.Text.Trim())
                {
                    return false;
                }

                searchIdx++;
                sourceIdx++;
            }

            return searchIdx == searchLines.Count;
        }

        private List<LineInfo> GetLines(string text)
        {
            var lines = new List<LineInfo>();
            int start = 0;
            while (start < text.Length)
            {
                int end = text.IndexOf('\n', start);
                if (end < 0) end = text.Length - 1;
                
                int len = end - start + 1;
                lines.Add(new LineInfo { Start = start, Length = len, Text = text.Substring(start, len) });
                start = end + 1;
            }
            return lines;
        }

        private class LineInfo
        {
            public int Start { get; set; }
            public int Length { get; set; }
            public string Text { get; set; }
        }
    }
}
