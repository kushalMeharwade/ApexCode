using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AiAssistant.Engine.SkeletonEngine.TreeSitter;

namespace AiAssistant.Engine.SkeletonEngine;

public static class ParserRouter
{
    private static readonly CSharpParser CSharp = new();
    private static readonly MarkupParser Markup = new("");
    private static readonly Dictionary<string, TreeSitterParser> TreeSitterParsers = new(StringComparer.OrdinalIgnoreCase);

    public static SkeletonResult Parse(string path, string source, int maxBudget = int.MaxValue, bool ignoreLimits = false)
    {
        var extension = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
        
        var lines = source.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        int totalLines = lines.Length;
        if (source.Length == 0) totalLines = 0;

        // D. MANIFEST ONLY (Minified check) - Runs BEFORE native parsing
        bool isMinifiedExtension = extension == ".map" || extension == ".lock" || 
                                   path.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase) || 
                                   path.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase) || 
                                   path.EndsWith(".bundle.js", StringComparison.OrdinalIgnoreCase) || 
                                   path.EndsWith(".pack.js", StringComparison.OrdinalIgnoreCase);
        
        int maxLineLength = lines.Length > 0 ? lines.Max(l => l.Length) : 0;
        double avgLineLength = lines.Length > 0 ? (double)source.Length / lines.Length : 0;

        bool isMinified = isMinifiedExtension || 
                          (lines.Length <= 20 && avgLineLength > 300) || 
                          (maxLineLength > 2000);

        if (isMinified && !ignoreLimits)
        {
            return new SkeletonResult
            {
                Path = path,
                ContentHash = ContentHash.Compute(source),
                Outline = "",
                StatusKind = ContextStatus.Omitted,
                StatusDetail = "OMITTED — minified asset; content not injected",
                TotalLines = totalLines,
                IncludedLines = 0
            };
        }

        SkeletonResult result = null;
        if (string.IsNullOrEmpty(extension))
        {
            return ParseFallback(path, source, maxBudget, lines, totalLines, ignoreLimits);
        }
        else if (CSharp.Supports(extension))
        {
            result = CSharp.Parse(source, extension, ignoreLimits);
        }
        else if (Markup.Supports(extension))
        {
            if (source.Length <= maxBudget && !ignoreLimits)
            {
                result = new SkeletonResult
                {
                    Path = path,
                    ContentHash = ContentHash.Compute(source),
                    Outline = source,
                    StatusKind = ContextStatus.Full,
                    StatusDetail = "RAW — full content",
                    TotalLines = totalLines,
                    IncludedLines = totalLines
                };
            }
            else
            {
                result = new MarkupParser(extension).Parse(source, extension, ignoreLimits);
            }
        }
        else if (TreeSitterLanguages.IsSupported(extension))
        {
            if (!TreeSitterParsers.TryGetValue(extension, out var parser))
            {
                parser = new TreeSitterParser(extension);
                TreeSitterParsers[extension] = parser;
            }
            result = parser.Parse(source, extension, ignoreLimits);
        }
        else
        {
            return ParseFallback(path, source, maxBudget, lines, totalLines, ignoreLimits);
        }

        // Enforce budget on native parsers
        result.TotalLines = source.Count(c => c == '\n') + (source.EndsWith("\n") ? 0 : 1);
        if (source.Length == 0) result.TotalLines = 0;
        
        if (!ignoreLimits && result.Outline.Length > maxBudget)
        {
            result.Outline = CutAtLastCompleteLine(result.Outline, maxBudget);
            result.StatusKind = ContextStatus.SkeletonTruncated;
            result.StatusDetail = "SKELETON — TRUNCATED; remainder omitted";
        }
        else if (result.StatusKind != ContextStatus.SkeletonTruncated)
        {
            result.StatusKind = ContextStatus.Skeleton;
            result.StatusDetail = "SKELETON — complete outline";
        }
        
        return result;
    }

    private static SkeletonResult ParseFallback(string path, string source, int maxBudget, string[] lines, int totalLines, bool ignoreLimits)
    {
        // A. RAW — FULL
        if (source.Length <= maxBudget || ignoreLimits)
        {
            return new SkeletonResult
            {
                Path = path,
                ContentHash = ContentHash.Compute(source),
                Outline = source,
                StatusKind = ContextStatus.Full,
                StatusDetail = "RAW — full content",
                TotalLines = totalLines,
                IncludedLines = totalLines
            };
        }

        // C. SMART EXCERPT
        var sb = new StringBuilder();
        int includedLines = 0;
        int startLine = 0;
        
        // Fallback: prioritize <body> for HTML
        if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].IndexOf("<body", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    startLine = i;
                    break;
                }
            }
        }

        for (int i = startLine; i < lines.Length; i++)
        {
            var line = lines[i];
            // +1 for newline character
            if (sb.Length + line.Length + 1 > maxBudget && !ignoreLimits)
            {
                break;
            }
            sb.AppendLine(line);
            includedLines++;
        }

        // Fallback if maxBudget is smaller than even the first line
        if (includedLines == 0 && lines.Length > 0 && maxBudget > 0 && !ignoreLimits)
        {
            sb.Append(lines[0].Substring(0, Math.Min(lines[0].Length, maxBudget)));
            includedLines = 1;
        }

        return new SkeletonResult
        {
            Path = path,
            ContentHash = ContentHash.Compute(source),
            Outline = sb.ToString().TrimEnd('\r', '\n'),
            StatusKind = ContextStatus.Excerpt,
            StatusDetail = $"EXCERPT — lines 1–{includedLines} of {totalLines}; remainder omitted",
            TotalLines = totalLines,
            IncludedLines = includedLines
        };
    }

    private static string CutAtLastCompleteLine(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || maxLength >= text.Length) return text;
        
        int lastNewline = text.LastIndexOf('\n', maxLength);
        if (lastNewline > 0)
        {
            return text.Substring(0, lastNewline);
        }
        
        return text.Substring(0, maxLength);
    }
}
