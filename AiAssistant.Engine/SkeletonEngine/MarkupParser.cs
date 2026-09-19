using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AiAssistant.Engine.SkeletonEngine.TreeSitter;

namespace AiAssistant.Engine.SkeletonEngine
{
    public class MarkupParser : ISkeletonParser
    {
        private readonly string _extension;

        public MarkupParser(string extension)
        {
            _extension = extension.ToLowerInvariant();
        }

        public bool Supports(string extension)
        {
            var ext = extension.ToLowerInvariant();
            return ext == ".html" || ext == ".htm" || ext == ".css" || ext == ".json";
        }

        public SkeletonResult Parse(string source, string extension, bool ignoreLimits = false)
        {
            var hash = ContentHash.Compute(source);
            var lines = source.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            
            var sb = new StringBuilder();
            var symbols = new List<SymbolNode>();
            
            if (_extension == ".html" || _extension == ".htm")
            {
                ParseHtml(lines, sb, symbols);
            }
            else if (_extension == ".css")
            {
                ParseCss(lines, sb, symbols);
            }
            else if (_extension == ".json")
            {
                ParseJson(lines, sb, symbols, ignoreLimits);
            }
            else
            {
                sb.Append(source); // Fallback
            }

            return new SkeletonResult
            {
                Path = "",
                ContentHash = hash,
                Outline = sb.ToString().TrimEnd(),
                Symbols = symbols,
                TotalLines = lines.Length,
                IncludedLines = lines.Length,
                StatusKind = ContextStatus.Skeleton,
                StatusDetail = "SKELETON — complete outline"
            };
        }

        private void ParseHtml(string[] lines, StringBuilder sb, List<SymbolNode> symbols)
        {
            var tagRegex = new Regex(@"<\s*([a-zA-Z0-9-]+)([^>]*)>", RegexOptions.Compiled);
            var idRegex = new Regex(@"id\s*=\s*['""]([^'""]+)['""]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            var classRegex = new Regex(@"class\s*=\s*['""]([^'""]+)['""]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            
            var significantTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
            { 
                "html", "head", "body", "div", "section", "article", "nav", "header", "footer", "main", "aside", 
                "form", "table", "ul", "ol", "h1", "h2", "h3", "h4", "h5", "h6", "button", "a", "label", "tr"
            };

            string lastTag = "";
            int repeatCount = 0;
            
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var matches = tagRegex.Matches(line);
                foreach (Match match in matches)
                {
                    var tag = match.Groups[1].Value;
                    if (!significantTags.Contains(tag)) continue;
                    
                    var attrs = match.Groups[2].Value;
                    var idMatch = idRegex.Match(attrs);
                    var classMatch = classRegex.Match(attrs);
                    
                    var idStr = idMatch.Success ? $" id=\"{idMatch.Groups[1].Value}\"" : "";
                    var classStr = classMatch.Success ? $" class=\"{classMatch.Groups[1].Value}\"" : "";
                    
                    var signature = $"<{tag}{idStr}{classStr}>";
                    
                    if (signature == lastTag && lastTag != "")
                    {
                        repeatCount++;
                    }
                    else
                    {
                        if (repeatCount > 0)
                        {
                            sb.AppendLine($"  ... {lastTag} x{repeatCount + 1} COLLAPSED");
                        }
                        sb.AppendLine(signature);
                        
                        var name = idMatch.Success ? idMatch.Groups[1].Value : (classMatch.Success ? classMatch.Groups[1].Value : tag);
                        symbols.Add(new SymbolNode(SymbolKind.Struct, name, signature, i + 1, i + 1));
                        
                        lastTag = signature;
                        repeatCount = 0;
                    }
                }
            }
            if (repeatCount > 0)
            {
                sb.AppendLine($"  ... {lastTag} x{repeatCount + 1} COLLAPSED");
            }
        }

        private void ParseCss(string[] lines, StringBuilder sb, List<SymbolNode> symbols)
        {
            var selectorRegex = new Regex(@"^([^{]+)\s*\{", RegexOptions.Compiled);
            
            for (int i = 0; i < lines.Length; i++)
            {
                var match = selectorRegex.Match(lines[i]);
                if (match.Success)
                {
                    var selector = match.Groups[1].Value.Trim();
                    sb.AppendLine($"{selector} {{ ... }}");
                    symbols.Add(new SymbolNode(SymbolKind.Struct, selector, selector, i + 1, i + 1));
                }
            }
        }

        private void ParseJson(string[] lines, StringBuilder sb, List<SymbolNode> symbols, bool ignoreLimits)
        {
            var keyRegex = new Regex(@"^\s*""([^""]+)""\s*:", RegexOptions.Compiled);
            
            int depth = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var match = keyRegex.Match(line);
                if (match.Success)
                {
                    var key = match.Groups[1].Value;
                    if (depth <= 2 || ignoreLimits) // Limit depth
                    {
                        sb.AppendLine(new string(' ', depth * 2) + $"\"{key}\": ...");
                        symbols.Add(new SymbolNode(SymbolKind.Struct, key, key, i + 1, i + 1));
                    }
                }
                depth += line.Count(c => c == '{' || c == '[');
                depth -= line.Count(c => c == '}' || c == ']');
                if (depth < 0) depth = 0;
            }
        }
    }
}
