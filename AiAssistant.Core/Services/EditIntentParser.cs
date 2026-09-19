using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiAssistant.Core.Models;

namespace AiAssistant.Core.Services
{
    public class EditIntentParseResult
    {
        public bool Success { get; set; }
        public EditIntent? Intent { get; set; }
        public string? ErrorMessage { get; set; }
        public string? RawOutput { get; set; }
    }

    public class EditIntentParser
    {
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            PropertyNameCaseInsensitive = true
        };

        public EditIntentParseResult Parse(string rawOutput)
        {
            if (string.IsNullOrWhiteSpace(rawOutput))
            {
                return new EditIntentParseResult { Success = false, ErrorMessage = "Raw output is empty.", RawOutput = rawOutput };
            }

            var trimmed = rawOutput.Trim();
            
            // Try 1: Direct JSON parsing
            if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
            {
                var result = TryParseJson(trimmed, rawOutput);
                if (result != null) return result;
            }

            // Try 2: Extract from markdown fences
            var fenceMatch = Regex.Match(trimmed, @"```(?:json)?\s*(.*?)\s*```", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (fenceMatch.Success)
            {
                var extracted = fenceMatch.Groups[1].Value.Trim();
                if (extracted.StartsWith("{"))
                {
                    var result = TryParseJson(extracted, rawOutput);
                    if (result != null) return result;
                }
            }

            // Try 3: Outermost curly braces block
            int firstBrace = trimmed.IndexOf('{');
            int lastBrace = trimmed.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                var extracted = trimmed.Substring(firstBrace, lastBrace - firstBrace + 1);
                var result = TryParseJson(extracted, rawOutput);
                if (result != null) return result;
            }

            // Try 4: Balanced brace extraction (fallback)
            string? balancedExtracted = ExtractBalancedBraces(trimmed);
            if (!string.IsNullOrEmpty(balancedExtracted))
            {
                var result = TryParseJson(balancedExtracted!, rawOutput);
                if (result != null) return result;
            }

            return new EditIntentParseResult { Success = false, ErrorMessage = "Could not locate valid JSON structure in the output.", RawOutput = rawOutput };
        }

        private EditIntentParseResult? TryParseJson(string jsonString, string rawOutput)
        {
            try
            {
                var intent = JsonSerializer.Deserialize<EditIntent>(jsonString, _jsonOptions);
                
                if (intent == null) return null;
                
                // Validate
                if ((intent.Edits == null || intent.Edits.Count == 0) && string.IsNullOrWhiteSpace(intent.Explanation))
                {
                    return new EditIntentParseResult { Success = false, ErrorMessage = "Empty edits list without an explanation is invalid.", RawOutput = rawOutput };
                }
                
                // Normalize and validate edits
                if (intent.Edits != null)
                {
                    foreach (var edit in intent.Edits)
                    {
                        if (string.IsNullOrWhiteSpace(edit.FilePath))
                        {
                            return new EditIntentParseResult { Success = false, ErrorMessage = "FilePath must not be empty.", RawOutput = rawOutput };
                        }
                        
                        if (string.IsNullOrEmpty(edit.Search))
                        {
                            return new EditIntentParseResult { Success = false, ErrorMessage = "Search string must not be empty.", RawOutput = rawOutput };
                        }
                        
                        if (edit.Search!.Length > 10000)
                        {
                            return new EditIntentParseResult { Success = false, ErrorMessage = "Search string exceeds 10,000 characters. Scope is too large.", RawOutput = rawOutput };
                        }
                        
                        // Normalize FilePath
                        edit.FilePath = edit.FilePath!.Replace("\\", "/");
                        
                        // Normalize Search and Replace
                        edit.Search = edit.Search.TrimEnd('\r', '\n', ' ', '\t');
                        edit.Replace = edit.Replace?.TrimEnd('\r', '\n', ' ', '\t') ?? string.Empty;
                    }
                }
                else
                {
                    intent.Edits = new System.Collections.Generic.List<EditOperation>();
                }
                
                return new EditIntentParseResult { Success = true, Intent = intent, RawOutput = rawOutput };
            }
            catch (JsonException)
            {
                return null; // Let the caller try the next strategy
            }
        }
        
        private string? ExtractBalancedBraces(string input)
        {
            int depth = 0;
            int startIndex = -1;
            for (int i = 0; i < input.Length; i++)
            {
                if (input[i] == '{')
                {
                    if (depth == 0) startIndex = i;
                    depth++;
                }
                else if (input[i] == '}')
                {
                    depth--;
                    if (depth == 0 && startIndex != -1)
                    {
                        return input.Substring(startIndex, i - startIndex + 1);
                    }
                }
            }
            return null;
        }
    }
}
