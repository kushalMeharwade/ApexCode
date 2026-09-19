using AiAssistant.Engine.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

namespace AiAssistant.Engine.Services;

public class RoslynEditService : IRoslynEditService
{
    // 10 MB limit to prevent OOM when editing very large files
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;

    public static ReplacementValidationResult TryBuildBatchReplacement(string content, List<BatchEditRequest> edits, int? scopeStartLine = null, int? scopeEndLine = null)
    {
        var result = new ReplacementValidationResult();
        string activeContent = content;
        
        // Scope optimization for large files
        int offsetAdjustment = 0;
        if (scopeStartLine.HasValue && scopeEndLine.HasValue)
        {
            var lines = content.Split('\n');
            int startLine = System.Math.Max(0, scopeStartLine.Value - 1);
            int endLine = System.Math.Min(lines.Length - 1, scopeEndLine.Value - 1);
            
            // Adjust bounds slightly for safety (e.g. 50 lines before/after)
            startLine = System.Math.Max(0, startLine - 50);
            endLine = System.Math.Min(lines.Length - 1, endLine + 50);
            
            int charIndex = 0;
            for (int i = 0; i < startLine; i++)
            {
                charIndex += lines[i].Length + 1; // +1 for \n
            }
            offsetAdjustment = charIndex;
            
            int length = 0;
            for (int i = startLine; i <= endLine; i++)
            {
                length += lines[i].Length + (i == lines.Length - 1 && !content.EndsWith("\n") ? 0 : 1);
            }
            
            // Ensure length doesn't overflow content (in case of \r\n discrepancies)
            if (offsetAdjustment + length > content.Length)
                length = content.Length - offsetAdjustment;
                
            activeContent = content.Substring(offsetAdjustment, length);
        }

        var allSpans = new List<ReplacementSpan>();

        foreach (var edit in edits)
        {
            // Set expected if not provided
            var valResult = TryBuildReplacement(activeContent, edit.OldText, edit.NewText, edit.ExpectedOccurrences);
            if (!valResult.Success)
            {
                return valResult; // Fast fail the whole batch on first edit failure
            }

            foreach (var span in valResult.Spans)
            {
                span.StartIndex += offsetAdjustment;
                
                // Overlap check
                if (allSpans.Any(s => 
                    (span.StartIndex >= s.StartIndex && span.StartIndex < s.StartIndex + s.Length) ||
                    (s.StartIndex >= span.StartIndex && s.StartIndex < span.StartIndex + span.Length)))
                {
                    return new ReplacementValidationResult 
                    { 
                        Success = false, 
                        Reason = "overlapping_edits", 
                        ErrorMessage = "Multiple edits resulted in overlapping target spans. Batch application aborted." 
                    };
                }
                
                allSpans.Add(span);
            }
        }

        // Apply safely bottom-up
        var sortedSpans = allSpans.OrderByDescending(s => s.StartIndex).ToList();
        var sb = new System.Text.StringBuilder(content);
        foreach (var span in sortedSpans)
        {
            sb.Remove(span.StartIndex, span.Length);
            sb.Insert(span.StartIndex, span.NewText);
        }

        result.Success = true;
        result.NewContent = sb.ToString();
        result.Spans = sortedSpans;
        return result;
    }

    public static ReplacementValidationResult TryBuildReplacement(string content, string oldText, string newText, int? expectedOccurrences)
    {
        int expected = expectedOccurrences ?? 1;
        var result = new ReplacementValidationResult { ExpectedOccurrences = expected };

        // Tier 0: Exact Match
        result.TiersAttempted.Add("exact");
        var exactRes = TryApplyMatch(content, oldText, newText, expected, StringComparison.Ordinal);
        if (exactRes.Success || exactRes.Reason == "occurrence_mismatch") 
        {
            exactRes.TiersAttempted = result.TiersAttempted;
            return exactRes;
        }

        // Tier 1: Whitespace Normalized Match (CRLF/LF, trailing spaces, tabs/spaces)
        result.TiersAttempted.Add("whitespace");
        var normalizedOld = NormalizeWhitespace(oldText);
        var normalizedContent = NormalizeWhitespace(content);
        var t1Res = TryApplyNormalizedMatch(content, normalizedContent, normalizedOld, newText, expected);
        if (t1Res.Success || t1Res.Reason == "occurrence_mismatch")
        {
            t1Res.TiersAttempted = result.TiersAttempted;
            return t1Res;
        }

        // Tier 2: Indentation-Relative Fuzzy Match
        result.TiersAttempted.Add("indentation");
        var t2Res = TryApplyTier2Match(content, oldText, newText, expected);
        if (t2Res.Success || t2Res.Reason == "occurrence_mismatch")
        {
            t2Res.TiersAttempted = result.TiersAttempted;
            return t2Res;
        }

        // Tier 3: Roslyn Structural Anchor (C# only, best effort)
        result.TiersAttempted.Add("roslyn");
        var t3Res = TryApplyTier3Match(content, oldText, newText, expected);
        if (t3Res.Success)
        {
            t3Res.TiersAttempted = result.TiersAttempted;
            return t3Res;
        }

        result.Success = false;
        result.Reason = "no_match_any_tier";
        result.ErrorMessage = $"Text not found in file (tried Exact, Whitespace, Indentation, and Roslyn tiers).";
        
        // Find closest match
        result.ClosestMatch = FindClosestMatch(content, oldText);

        return result;
    }

    private static string NormalizeWhitespace(string text, int tabWidth = 4)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            // Normalize tabs to spaces
            lines[i] = lines[i].Replace("\t", new string(' ', tabWidth));
            // Trim trailing whitespace
            lines[i] = lines[i].TrimEnd();
        }
        return string.Join("\n", lines);
    }

    private static ReplacementValidationResult TryApplyMatch(string content, string oldText, string newText, int expected, StringComparison comparison)
    {
        var occurrences = CountOccurrences(content, oldText, comparison);
        if (occurrences == 0) return new ReplacementValidationResult { Success = false, Reason = "no_match" };

        if (occurrences != expected)
        {
            return new ReplacementValidationResult
            {
                Success = false,
                Reason = "occurrence_mismatch",
                ExpectedOccurrences = expected,
                FoundOccurrences = occurrences,
                ErrorMessage = $"Expected {expected} occurrence(s) of the target text but found {occurrences}.",
                MatchPreviews = GetMatchPreviews(content, oldText, comparison)
            };
        }

        string result = content;
        // Replace in reverse offset order to handle multiple occurrences
        var matches = new List<int>();
        int pos = 0;
        while ((pos = content.IndexOf(oldText, pos, comparison)) >= 0)
        {
            matches.Add(pos);
            pos += oldText.Length;
        }

        var spans = new List<ReplacementSpan>();
        var sb = new System.Text.StringBuilder(content);
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            int index = matches[i];
            spans.Add(new ReplacementSpan { StartIndex = index, Length = oldText.Length, NewText = newText });
            sb.Remove(index, oldText.Length);
            sb.Insert(index, newText);
        }

        spans.Reverse(); // Since we added them bottom-up
        return new ReplacementValidationResult { Success = true, NewContent = sb.ToString(), Spans = spans };
    }

    private static ReplacementValidationResult TryApplyNormalizedMatch(string originalContent, string normalizedContent, string normalizedOld, string newText, int expected)
    {
        var occurrences = CountOccurrences(normalizedContent, normalizedOld, StringComparison.Ordinal);
        if (occurrences == 0) return new ReplacementValidationResult { Success = false, Reason = "no_match" };

        var pattern = string.Join(@"[ \t]*\r?\n", normalizedOld.Split('\n').Select(l => System.Text.RegularExpressions.Regex.Escape(l)));
        var regex = new System.Text.RegularExpressions.Regex(pattern);
        var matches = regex.Matches(originalContent);

        if (matches.Count != expected)
        {
            return new ReplacementValidationResult
            {
                Success = false,
                Reason = "occurrence_mismatch",
                ExpectedOccurrences = expected,
                FoundOccurrences = matches.Count,
                ErrorMessage = $"Expected {expected} occurrence(s) but found {matches.Count} (Tier 1).",
                MatchPreviews = matches.Cast<System.Text.RegularExpressions.Match>().Select(m => new MatchPreview { Line = GetLineNumber(originalContent, m.Index), Preview = GetPreview(originalContent, m.Index) }).ToList()
            };
        }

        var spans = new List<ReplacementSpan>();
        var sb = new System.Text.StringBuilder(originalContent);
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            var match = matches[i];
            spans.Add(new ReplacementSpan { StartIndex = match.Index, Length = match.Length, NewText = newText });
            sb.Remove(match.Index, match.Length);
            sb.Insert(match.Index, newText);
        }

        spans.Reverse();
        return new ReplacementValidationResult { Success = true, NewContent = sb.ToString(), Spans = spans };
    }

    private static ReplacementValidationResult TryApplyTier2Match(string content, string oldText, string newText, int expected)
    {
        string dedent(string text, out string minIndent)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var indents = lines.Where(l => !string.IsNullOrWhiteSpace(l))
                               .Select(l => new string(l.TakeWhile(char.IsWhiteSpace).ToArray()))
                               .ToList();
            minIndent = indents.Any() ? indents.OrderBy(i => i.Length).First() : "";
            string min = minIndent; // capture for lambda
            return string.Join("\n", lines.Select(l => l.StartsWith(min) && l.Length >= min.Length ? l.Substring(min.Length) : l));
        }

        var dedentedOld = dedent(oldText, out var oldMinIndent).TrimEnd();
        var dedentedNew = dedent(newText, out var newMinIndent);

        var linesOld = dedentedOld.Split('\n');
        
        var patternSb = new System.Text.StringBuilder();
        patternSb.Append(@"(?<indent>[ \t]*)");
        
        for (int i = 0; i < linesOld.Length; i++)
        {
            var line = linesOld[i];
            var relativeIndent = new string(line.TakeWhile(char.IsWhiteSpace).ToArray());
            var actualText = line.Length >= relativeIndent.Length ? line.Substring(relativeIndent.Length).TrimEnd() : string.Empty;
            
            if (i > 0)
            {
                patternSb.Append(@"\r?\n\k<indent>");
                if (relativeIndent.Length > 0)
                {
                    // Match any whitespace of similar length for relative indentation
                    patternSb.Append(@"[ \t]{").Append(relativeIndent.Length).Append(@"}");
                }
            }
            else if (relativeIndent.Length > 0)
            {
                patternSb.Append(@"[ \t]{").Append(relativeIndent.Length).Append(@"}");
            }
            
            // Handle empty or whitespace-only lines explicitly
            if (string.IsNullOrEmpty(actualText))
            {
                patternSb.Append(@"[ \t]*");
            }
            else
            {
                patternSb.Append(System.Text.RegularExpressions.Regex.Escape(actualText));
                patternSb.Append(@"[ \t]*");
            }
        }
        
        var pattern = patternSb.ToString();

        System.Text.RegularExpressions.Regex regex;
        try
        {
            regex = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return new ReplacementValidationResult { Success = false, Reason = "regex_error", ErrorMessage = $"Tier 2 regex construction failed: {ex.Message}" };
        }

        System.Text.RegularExpressions.MatchCollection matches;
        try
        {
            matches = regex.Matches(content);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return new ReplacementValidationResult { Success = false, Reason = "regex_timeout", ErrorMessage = "Tier 2 regex matching timed out (potential ReDoS). Try providing more specific oldText." };
        }

        if (matches.Count == 0) return new ReplacementValidationResult { Success = false, Reason = "no_match" };
        if (matches.Count != expected)
        {
            return new ReplacementValidationResult
            {
                Success = false,
                Reason = "occurrence_mismatch",
                ExpectedOccurrences = expected,
                FoundOccurrences = matches.Count,
                ErrorMessage = $"Expected {expected} occurrence(s) but found {matches.Count} (Tier 2).",
                MatchPreviews = matches.Cast<System.Text.RegularExpressions.Match>().Select(m => new MatchPreview { Line = GetLineNumber(content, m.Index), Preview = GetPreview(content, m.Index) }).ToList()
            };
        }

        var spans = new List<ReplacementSpan>();
        var sb = new System.Text.StringBuilder(content);
        var newLines = dedentedNew.Split('\n');
        
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            var match = matches[i];
            string baseIndent = match.Groups["indent"].Value;
            
            // Properly re-indent each line of newText with base indentation
            var indentedLines = new List<string>();
            for (int j = 0; j < newLines.Length; j++)
            {
                string line = newLines[j];
                // Extract the relative indentation from this line of newText
                string lineRelativeIndent = new string(line.TakeWhile(char.IsWhiteSpace).ToArray());
                string lineContent = line.Length >= lineRelativeIndent.Length ? line.Substring(lineRelativeIndent.Length) : string.Empty;
                
                // Re-apply: base indent from match + relative indent from newText line
                indentedLines.Add(baseIndent + lineRelativeIndent + lineContent);
            }
            
            string indentedNewText = string.Join("\n", indentedLines);
            
            // Fix line endings to match the matched region
            if (match.Value.Contains("\r\n"))
            {
                indentedNewText = indentedNewText.Replace("\n", "\r\n");
            }

            spans.Add(new ReplacementSpan { StartIndex = match.Index, Length = match.Length, NewText = indentedNewText });
            
            sb.Remove(match.Index, match.Length);
            sb.Insert(match.Index, indentedNewText);
        }
        
        spans.Reverse();
        return new ReplacementValidationResult { Success = true, NewContent = sb.ToString(), Spans = spans };
    }

    private static ReplacementValidationResult TryApplyTier3Match(string content, string oldText, string newText, int expected)
    {
        try
        {
            // Simple heuristic: if oldText looks like a method or property, we try to match its signature
            var oldTree = CSharpSyntaxTree.ParseText(oldText);
            var oldRoot = oldTree.GetRoot();
            var parsedMember = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseMemberDeclaration(oldText);
            
            string? memberName = null;
            if (parsedMember is Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax methodDecl)
                memberName = methodDecl.Identifier.Text;
            else if (parsedMember is Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax propDecl)
                memberName = propDecl.Identifier.Text;
            else if (parsedMember is Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax classDecl)
                memberName = classDecl.Identifier.Text;

            if (memberName != null)
            {
                var contentTree = CSharpSyntaxTree.ParseText(content);
                var contentRoot = contentTree.GetRoot();

                // Find all matching members in the target file
                var targetMembers = contentRoot.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax>()
                    .Where(m =>
                    {
                        if (m is Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax md && md.Identifier.Text == memberName) return true;
                        if (m is Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax pd && pd.Identifier.Text == memberName) return true;
                        if (m is Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax cd && cd.Identifier.Text == memberName) return true;
                        return false;
                    }).ToList();

                if (targetMembers.Count > 0)
                {
                    var allSpans = new List<ReplacementSpan>();
                    var sb = new System.Text.StringBuilder(content);
                    int validMatches = 0;

                    foreach (var targetMember in targetMembers)
                    {
                        var targetSpan = targetMember.FullSpan;
                        string scopedContent = content.Substring(targetSpan.Start, targetSpan.Length);
                        
                        var normalizedOld = NormalizeWhitespace(oldText);
                        var normalizedScope = NormalizeWhitespace(scopedContent);
                        
                        bool isFullNode = false;
                        if (normalizedOld.Length > 0 && normalizedScope.Length > 0)
                        {
                            double ratio = (double)normalizedOld.Length / normalizedScope.Length;
                            isFullNode = ratio >= 0.8 && ratio <= 1.2;
                        }

                        if (expected == 1 && isFullNode)
                        {
                            validMatches++;
                            allSpans.Add(new ReplacementSpan { StartIndex = targetSpan.Start, Length = targetSpan.Length, NewText = newText });
                        }
                        else
                        {
                            var scopedMatchResult = TryApplyTier2Match(scopedContent, oldText, newText, 1);
                            
                            if (scopedMatchResult.Success)
                            {
                                validMatches++;
                                foreach (var s in scopedMatchResult.Spans)
                                {
                                    allSpans.Add(new ReplacementSpan { StartIndex = targetSpan.Start + s.StartIndex, Length = s.Length, NewText = s.NewText });
                                }
                            }
                        }
                    }

                    if (validMatches == expected)
                    {
                        var sortedSpans = allSpans.OrderByDescending(s => s.StartIndex).ToList();
                        foreach (var span in sortedSpans)
                        {
                            sb.Remove(span.StartIndex, span.Length);
                            sb.Insert(span.StartIndex, span.NewText);
                        }

                        return new ReplacementValidationResult { Success = true, NewContent = sb.ToString(), Spans = sortedSpans };
                    }
                    else
                    {
                        return new ReplacementValidationResult { Success = false, ErrorMessage = $"Tier 3 found {targetMembers.Count} member(s) named '{memberName}', but only {validMatches} matched the actual oldText (expected {expected})." };
                    }
                }
                else
                {
                    return new ReplacementValidationResult { Success = false, ErrorMessage = $"Tier 3 Roslyn match expected {expected} member(s) named '{memberName}' but found {targetMembers.Count}." };
                }
            }
            
            return new ReplacementValidationResult { Success = false, ErrorMessage = "Tier 3 Roslyn match failed: oldText does not parse as a recognizable C# member declaration (Method, Property, Class)." };
        }
        catch (Exception ex)
        {
            return new ReplacementValidationResult { Success = false, ErrorMessage = $"Tier 3 Roslyn match threw an exception: {ex.Message}" };
        }
    }

    private static int CountOccurrences(string haystack, string needle, StringComparison comparison = StringComparison.Ordinal)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        int count = 0, pos = 0;
        while ((pos = haystack.IndexOf(needle, pos, comparison)) >= 0)
        {
            count++;
            pos += needle.Length;
        }
        return count;
    }

    private static List<MatchPreview> GetMatchPreviews(string content, string oldText, StringComparison comparison)
    {
        var previews = new List<MatchPreview>();
        int pos = 0;
        while ((pos = content.IndexOf(oldText, pos, comparison)) >= 0)
        {
            previews.Add(new MatchPreview { Line = GetLineNumber(content, pos), Preview = GetPreview(content, pos) });
            pos += oldText.Length;
        }
        return previews;
    }

    private static int GetLineNumber(string content, int index)
    {
        if (index < 0 || index >= content.Length) return 1;
        return content.Substring(0, index).Count(c => c == '\n') + 1;
    }

    private static string GetPreview(string content, int index)
    {
        if (index < 0 || index >= content.Length) return string.Empty;
        var start = content.LastIndexOf('\n', index);
        start = start == -1 ? 0 : start + 1;
        var end = content.IndexOf('\n', index);
        end = end == -1 ? content.Length : end;
        return content.Substring(start, end - start).Trim();
    }

    private static ClosestMatchResult? FindClosestMatch(string content, string oldText)
    {
        var oldLines = oldText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (oldLines.Count == 0) return null;

        var contentLines = content.Split('\n');
        ClosestMatchResult? bestMatch = null;
        double bestSimilarity = 0.0;

        // Pre-filter: only compare against lines with similar length
        var targetLine = oldLines[0];
        
        for (int i = 0; i < contentLines.Length; i++)
        {
            var line = contentLines[i].Trim();
            if (line.Length == 0) continue;

            // Fast pre-filter: line length delta within 30%
            double lengthRatio = (double)Math.Min(line.Length, targetLine.Length) / Math.Max(line.Length, targetLine.Length);
            if (lengthRatio < 0.7) continue;

            double similarity = CalculateSimilarity(line, targetLine);
            if (similarity > bestSimilarity)
            {
                bestSimilarity = similarity;
                bestMatch = new ClosestMatchResult
                {
                    Line = i + 1,
                    Similarity = similarity,
                    Preview = contentLines[i].Trim()
                };
            }
        }

        return bestMatch;
    }

    private static double CalculateSimilarity(string source, string target)
    {
        if (source == target) return 1.0;
        int distance = ComputeLevenshteinDistance(source, target);
        return 1.0 - ((double)distance / Math.Max(source.Length, target.Length));
    }

    private static int ComputeLevenshteinDistance(string source, string target)
    {
        if (string.IsNullOrEmpty(source)) return string.IsNullOrEmpty(target) ? 0 : target.Length;
        if (string.IsNullOrEmpty(target)) return source.Length;

        int[] v0 = new int[target.Length + 1];
        int[] v1 = new int[target.Length + 1];

        for (int i = 0; i < v0.Length; i++) v0[i] = i;

        for (int i = 0; i < source.Length; i++)
        {
            v1[0] = i + 1;
            for (int j = 0; j < target.Length; j++)
            {
                int cost = (source[i] == target[j]) ? 0 : 1;
                v1[j + 1] = Math.Min(v1[j] + 1, Math.Min(v0[j + 1] + 1, v0[j] + cost));
            }
            for (int j = 0; j < v0.Length; j++) v0[j] = v1[j];
        }
        return v1[target.Length];
    }

    public async Task<EditResult> ApplyEditAsync(string filePath, string oldText, string newText,
        int? expectedOccurrences = null, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(filePath))
                return new EditResult { Success = false, ErrorMessage = $"File not found: {filePath}" };

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > MaxFileSizeBytes)
                return new EditResult { Success = false, ErrorMessage = $"File too large to edit: {filePath} ({fileInfo.Length / (1024 * 1024)} MB)" };

            var content = await Task.Run(() => File.ReadAllText(filePath));

            var res = TryBuildReplacement(content, oldText, newText, expectedOccurrences);
            if (!res.Success)
            {
                return new EditResult { Success = false, ErrorMessage = res.ErrorMessage };
            }

            await Task.Run(() => AiAssistant.Storage.SafeFileWriter.WriteAllText(filePath, res.NewContent));

            return new EditResult
            {
                Success = true,
                FilePath = filePath,
                OriginalContent = content,
                NewContent = res.NewContent,
                LinesChanged = res.NewContent.Count(c => c == '\n') - content.Count(c => c == '\n')
            };
        }
        catch (Exception ex)
        {
            return new EditResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<EditResult> ReplaceTextAsync(string filePath, int startLine, int startColumn, int endLine, int endColumn, string newText, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(filePath))
                return new EditResult { Success = false, ErrorMessage = $"File not found: {filePath}" };

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > MaxFileSizeBytes)
                return new EditResult { Success = false, ErrorMessage = $"File too large to edit: {filePath} ({fileInfo.Length / (1024 * 1024)} MB)" };

            var content = await Task.Run(() => File.ReadAllText(filePath));
            var lines = content.Split('\n');

            // Validate line numbers are within bounds
            if (startLine < 1 || startLine > lines.Length)
                return new EditResult { Success = false, ErrorMessage = $"Start line {startLine} is out of range (file has {lines.Length} lines)" };
            if (endLine < 1 || endLine > lines.Length)
                return new EditResult { Success = false, ErrorMessage = $"End line {endLine} is out of range (file has {lines.Length} lines)" };
            if (startLine > endLine)
                return new EditResult { Success = false, ErrorMessage = $"Start line ({startLine}) cannot be greater than end line ({endLine})" };

            // Validate column numbers are within bounds
            var startLineLength = lines[startLine - 1].Length;
            var endLineLength = lines[endLine - 1].Length;
            if (startColumn < 1 || startColumn > startLineLength + 1)
                return new EditResult { Success = false, ErrorMessage = $"Start column {startColumn} is out of range (line {startLine} has {startLineLength} characters)" };
            if (endColumn < 1 || endColumn > endLineLength + 1)
                return new EditResult { Success = false, ErrorMessage = $"End column {endColumn} is out of range (line {endLine} has {endLineLength} characters)" };

            var startOffset = lines.Take(startLine - 1).Sum(l => l.Length + 1) + startColumn - 1;
            var endOffset = lines.Take(endLine - 1).Sum(l => l.Length + 1) + endColumn;

            // Final safety check on computed offsets
            if (startOffset < 0 || startOffset > content.Length || endOffset < 0 || endOffset > content.Length || startOffset > endOffset)
                return new EditResult { Success = false, ErrorMessage = $"Invalid offset range: startOffset={startOffset}, endOffset={endOffset}, contentLength={content.Length}" };

            var newContent = content.Substring(0, startOffset) + newText + content.Substring(endOffset);
            await Task.Run(() => AiAssistant.Storage.SafeFileWriter.WriteAllText(filePath, newContent));

            return new EditResult
            {
                Success = true,
                FilePath = filePath,
                OriginalContent = content,
                NewContent = newContent,
                LinesChanged = lines.Length - newContent.Split('\n').Length
            };
        }
        catch (Exception ex)
        {
            return new EditResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<EditResult> ReplaceEntireFileAsync(string filePath, string expectedSha, string newContent, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(filePath))
                return new EditResult { Success = false, ErrorMessage = $"File not found: {filePath}" };

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > MaxFileSizeBytes)
                return new EditResult { Success = false, ErrorMessage = $"File too large to edit: {filePath} ({fileInfo.Length / (1024 * 1024)} MB)" };

            var content = await Task.Run(() => File.ReadAllText(filePath));
            var currentSha = AiAssistant.Engine.SkeletonEngine.ContentHash.Compute(content);

            if (currentSha != expectedSha)
                return new EditResult { Success = false, ErrorMessage = $"Optimistic concurrency failure: file changed on disk. Expected {expectedSha}, got {currentSha}." };

            if (filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                var tree = CSharpSyntaxTree.ParseText(newContent, cancellationToken: ct);
                var diagnostics = tree.GetDiagnostics(ct);
                if (diagnostics.Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
                {
                    return new EditResult { Success = false, ErrorMessage = "Replacement contains syntax errors." };
                }
            }

            await Task.Run(() => AiAssistant.Storage.SafeFileWriter.WriteAllText(filePath, newContent));

            return new EditResult
            {
                Success = true,
                FilePath = filePath,
                OriginalContent = content,
                NewContent = newContent,
                LinesChanged = newContent.Count(c => c == '\n') - content.Count(c => c == '\n')
            };
        }
        catch (Exception ex)
        {
            return new EditResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<string> GetFileContentAsync(string filePath, CancellationToken ct = default)
    {
        return await Task.Run(() => File.ReadAllText(filePath), ct);
    }

    public async Task<IEnumerable<string>> FindUsagesAsync(string filePath, int line, int column, CancellationToken ct = default)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(directory)) return Array.Empty<string>();

            // Find the project file
            var projectFile = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (string.IsNullOrEmpty(projectFile))
            {
                // Traverse up to find a project
                var parent = Directory.GetParent(directory);
                while (parent != null && string.IsNullOrEmpty(projectFile))
                {
                    projectFile = Directory.GetFiles(parent.FullName, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
                    parent = parent.Parent;
                }
            }

            if (string.IsNullOrEmpty(projectFile)) return Array.Empty<string>();

            using var workspace = Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace.Create();
            var project = await workspace.OpenProjectAsync(projectFile, cancellationToken: ct);
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation == null) return Array.Empty<string>();

            var document = project.Documents.FirstOrDefault(d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (document == null) return Array.Empty<string>();

            var semanticModel = await document.GetSemanticModelAsync(ct);
            var syntaxTree = await document.GetSyntaxTreeAsync(ct);
            if (semanticModel == null || syntaxTree == null) return Array.Empty<string>();

            var text = await syntaxTree.GetTextAsync(ct);
            var position = text.Lines[line - 1].Start + column - 1;

            var root = await syntaxTree.GetRootAsync(ct);
            var token = root.FindToken(position);
            var symbol = semanticModel.GetSymbolInfo(token.Parent!).Symbol ?? semanticModel.GetDeclaredSymbol(token.Parent!);

            if (symbol == null) return Array.Empty<string>();

            var usages = await Microsoft.CodeAnalysis.FindSymbols.SymbolFinder.FindReferencesAsync(symbol, project.Solution, cancellationToken: ct);
            var results = new List<string>();

            foreach (var reference in usages)
            {
                foreach (var location in reference.Locations)
                {
                    var locText = await location.Document.GetTextAsync(ct);
                    var lineSpan = location.Location.GetLineSpan();
                    var lineText = locText.Lines[lineSpan.StartLinePosition.Line].ToString().Trim();
                    results.Add($"{location.Document.FilePath}:{lineSpan.StartLinePosition.Line + 1} - {lineText}");
                }
            }

            return results.Distinct();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] FindUsagesAsync failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    public async Task<IEnumerable<string>> GetCompilationErrorsAsync(string projectPath, CancellationToken ct = default)
    {
        try
        {
            var projectFile = projectPath;
            if (Directory.Exists(projectPath))
            {
                projectFile = Directory.GetFiles(projectPath, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (string.IsNullOrEmpty(projectFile)) return Array.Empty<string>();
            }

            using var workspace = Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace.Create();
            var project = await workspace.OpenProjectAsync(projectFile, cancellationToken: ct);
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation == null) return Array.Empty<string>();

            var diagnostics = compilation.GetDiagnostics(cancellationToken: ct);
            return diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"{d.Location.GetLineSpan().Path}({d.Location.GetLineSpan().StartLinePosition.Line + 1}): {d.GetMessage()}")
                .ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] GetCompilationErrorsAsync failed: {ex.Message}");
            return new[] { $"Failed to get compilation errors: {ex.Message}" };
        }
    }

    /// <summary>
    /// Applies a code edit using Roslyn for C# files. Parses the file into a syntax tree,
    /// finds the target node by text span, applies the replacement, and formats the result.
    /// Falls back to string-based replacement for non-C# files.
    /// Does NOT silently catch Roslyn errors — failures are returned as EditResult.Success=false
    /// so callers know whether they got an AST edit or a string edit.
    /// </summary>
    public async Task<EditResult> ApplyRoslynEditAsync(string filePath, string oldText, string newText,
        int? expectedOccurrences = null, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            return new EditResult { Success = false, ErrorMessage = $"File not found: {filePath}" };

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length > MaxFileSizeBytes)
            return new EditResult { Success = false, ErrorMessage = $"File too large: {filePath}" };

        // Only use Roslyn for C# files
        if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return await ApplyEditAsync(filePath, oldText, newText, expectedOccurrences, ct);

        var code = File.ReadAllText(filePath);

        // Occurrence guard (applied before any AST work)
        var occurrences = CountOccurrences(code, oldText);
        if (occurrences == 0)
            return new EditResult { Success = false, ErrorMessage = "Text not found in file (Roslyn)" };

        if (expectedOccurrences.HasValue && occurrences != expectedOccurrences.Value)
            return new EditResult
            {
                Success = false,
                ErrorMessage = $"Expected {expectedOccurrences.Value} occurrence(s) but found {occurrences}. " +
                               "Aborting to prevent silent multi-site corruption."
            };

        var tree = CSharpSyntaxTree.ParseText(code, path: filePath, cancellationToken: ct);
        var root = await tree.GetRootAsync(ct);

        if (root == null)
            return new EditResult { Success = false, ErrorMessage = "Roslyn failed to parse the file." };

        // Find the text span to replace
        var oldTextSpan = FindTextSpan(root, oldText);
        if (oldTextSpan == null)
            return new EditResult { Success = false, ErrorMessage = "Text not found in file (Roslyn AST)" };

        // Parse the replacement as a syntax node of the EXACT type as the node being replaced
        var newTextTrimmed = newText.Trim();
        var targetType = oldTextSpan.Value.node.GetType();
        
        SyntaxNode? replacementNode = null;

        if (oldTextSpan.Value.node is Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax)
        {
            replacementNode = SyntaxFactory.ParseMemberDeclaration(newTextTrimmed);
        }
        else if (oldTextSpan.Value.node is Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax)
        {
            replacementNode = SyntaxFactory.ParseStatement(newTextTrimmed);
        }
        else if (oldTextSpan.Value.node is Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax)
        {
            replacementNode = SyntaxFactory.ParseExpression(newTextTrimmed);
        }

        if (replacementNode == null)
        {
            var parsedRoot = await CSharpSyntaxTree.ParseText(newTextTrimmed).GetRootAsync(ct);
            replacementNode = parsedRoot.DescendantNodesAndSelf().FirstOrDefault(n => n.GetType() == targetType) ?? parsedRoot;
        }

        // Apply the edit — no catch/fallback: Roslyn errors surface as failures
        var newRoot = root.ReplaceNode(oldTextSpan.Value.node, replacementNode);
        var formattedRoot = Formatter.Format(newRoot, new AdhocWorkspace(), cancellationToken: ct);
        var newContent = formattedRoot.ToFullString();

        var newTree = CSharpSyntaxTree.ParseText(newContent, cancellationToken: ct);
        var diagnostics = newTree.GetDiagnostics(ct);
        if (diagnostics.Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
        {
            return new EditResult { Success = false, ErrorMessage = "AST edit resulted in invalid syntax. Aborting." };
        }

        AiAssistant.Storage.SafeFileWriter.WriteAllText(filePath, newContent);

        return new EditResult
        {
            Success = true,
            FilePath = filePath,
            OriginalContent = code,
            NewContent = newContent,
            LinesChanged = newContent.Split('\n').Length - code.Split('\n').Length
        };
    }

    /// <summary>
    /// Finds the syntax node in the tree that matches the given text.
    /// </summary>
    private static (SyntaxNode node, TextSpan span)? FindTextSpan(SyntaxNode root, string text)
    {
        var textSpan = root.FullSpan;
        var sourceText = root.SyntaxTree?.GetText();
        if (sourceText == null) return null;

        var index = sourceText.ToString().IndexOf(text, StringComparison.Ordinal);
        if (index < 0) return null;

        var targetSpan = new TextSpan(index, text.Length);
        var node = root.FindNode(targetSpan, getInnermostNodeForTie: true);

        // Validate string match length to prevent replacing a massive parent node
        if (node.Span.Length > text.Length * 1.5)
        {
            return null;
        }

        return (node, node.FullSpan);
    }

    /// <summary>
    /// Counts the number of non-overlapping occurrences of <paramref name="needle"/> inside
    /// <paramref name="haystack"/> using ordinal comparison.  Used by the occurrence-guard logic
    /// in <see cref="ApplyEditAsync"/> and <see cref="ApplyRoslynEditAsync"/> to detect
    /// accidental multi-site replacement before any write is performed.
    /// </summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        int count = 0;
        int pos = 0;
        while ((pos = haystack.IndexOf(needle, pos, StringComparison.Ordinal)) >= 0)
        {
            count++;
            pos += needle.Length; // advance past this match (non-overlapping)
        }
        return count;
    }
}
