using System;
using System.Collections.Generic;
using System.Text;

namespace AiAssistant.Core.Services;

/// <summary>
/// Represents a parsed XML-style tool call extracted from model text output.
/// </summary>
public class ParsedToolCall
{
    public string Name { get; set; } = "";
    public Dictionary<string, string> Params { get; set; } = new();
    public string CallId { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public bool Partial { get; set; } = false;
}

/// <summary>
/// Represents a content block parsed from model text output — either text or a tool call.
/// </summary>
public class ParsedContentBlock
{
    public bool IsText { get; set; }
    public string Text { get; set; } = "";
    public bool Partial { get; set; } = false;

    public bool IsToolCall { get; set; }
    public ParsedToolCall? ToolCall { get; set; }

    public static ParsedContentBlock CreateText(string text, bool partial = false) =>
        new() { IsText = true, Text = text, Partial = partial };

    public static ParsedContentBlock CreateToolCall(ParsedToolCall toolCall) =>
        new() { IsToolCall = true, ToolCall = toolCall };
}

/// <summary>
/// Parses assistant message text that may contain XML-style tool calls
/// into structured content blocks (text + tool_use).
///
/// This is a C# port of cline's parseAssistantMessageV2 algorithm.
/// It enables the agent to execute tools emitted as XML tags (e.g. &lt;read_files&gt;&lt;path&gt;file.cs&lt;/path&gt;&lt;/read_files&gt;)
/// instead of only native JSON tool calls, preventing the "hallucinated XML" penalty/retry cycle.
///
/// Key behaviors:
/// - Scans character-by-character using index-based tag matching
/// - Maintains a Map of known tool open tags and parameter open tags
/// - Handles arbitrary parameter names inside tool blocks
/// - Marks incomplete blocks as <c>partial</c> (useful during streaming)
/// - Strips tool XML from text that will be sent to the API (only text is kept for history)
/// </summary>
public static class AssistantMessageParser
{
    /// <summary>
    /// Parse an assistant message string that may contain mixed text and XML-style tool calls.
    /// </summary>
    /// <param name="assistantMessage">The raw text output from the LLM.</param>
    /// <param name="knownToolNames">Set of tool names the model is expected to emit. Only tags matching these names are parsed as tool calls.</param>
    /// <returns>Array of content blocks (text or tool_use).</returns>
    public static ParsedContentBlock[] Parse(string assistantMessage, ISet<string>? knownToolNames = null)
    {
        var contentBlocks = new List<ParsedContentBlock>();

        if (string.IsNullOrEmpty(assistantMessage))
            return contentBlocks.ToArray();

        // Build lookup maps for tool open tags
        var toolUseOpenTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (knownToolNames != null)
        {
            foreach (var name in knownToolNames)
            {
                if (!string.IsNullOrEmpty(name))
                    toolUseOpenTags[$"<{name}>"] = name;
            }
        }

        int len = assistantMessage.Length;
        int currentTextContentStart = 0;
        ParsedContentBlock? currentTextContent = null;
        ParsedToolCall? currentToolUse = null;
        int currentToolUseStart = 0; // Index after the opening tag
        string? currentParamName = null;
        int currentParamValueStart = 0;

        for (int i = 0; i < len; i++)
        {
            int currentCharIndex = i;

            // --- State: Parsing a Tool Parameter Value ---
            if (currentToolUse != null && currentParamName != null)
            {
                string closeTag = $"</{currentParamName}>";
                if (currentCharIndex >= closeTag.Length - 1 &&
                     assistantMessage.AsSpan(currentCharIndex - closeTag.Length + 1, closeTag.Length).SequenceEqual(closeTag.AsSpan()))
                {
                    // Found closing tag for parameter
                    string value = assistantMessage
                        .Substring(currentParamValueStart, currentCharIndex - currentParamValueStart - closeTag.Length + 1)
                        .Trim();
                    currentToolUse.Params[currentParamName] = value;
                    currentParamName = null;
                    continue;
                }
                else
                {
                    continue; // Still inside param value
                }
            }

            // --- State: Parsing a Tool Use (looking for params or close) ---
            if (currentToolUse != null && currentParamName == null)
            {
                // Check if starting a new parameter (any <param_name> tag)
                bool startedNewParam = false;
                int maxParamTagLen = Math.Min(i, 50); // Reasonable limit for param tag length
                for (int tagStart = Math.Max(0, i - 50); tagStart <= i; tagStart++)
                {
                    if (tagStart == i) continue; // Can't start at current position
                }

                // Simpler approach: scan backwards from current position for <tag>
                // Check for any opening param tag ending at position i
                for (int tagLen = 1; tagLen <= 40 && i - tagLen + 1 >= 0; tagLen++)
                {
                    string potentialTag = assistantMessage.Substring(i - tagLen + 1, tagLen);
                    if (potentialTag.StartsWith("<") && potentialTag.EndsWith(">") && !potentialTag.StartsWith("</"))
                    {
                        string paramName = potentialTag.Trim('<', '>', '/');
                        if (!string.IsNullOrEmpty(paramName) && IsValidParamName(paramName))
                        {
                            currentParamName = paramName;
                            currentParamValueStart = i + 1;
                            startedNewParam = true;
                            break;
                        }
                    }
                }

                if (startedNewParam) continue;

                // Check if closing the current tool use
                string toolCloseTag = $"</{currentToolUse.Name}>";
                if (currentCharIndex >= toolCloseTag.Length - 1 &&
                    assistantMessage.AsSpan(currentCharIndex - toolCloseTag.Length + 1, toolCloseTag.Length).SequenceEqual(toolCloseTag.AsSpan()))
                {
                    int toolContentStart = currentToolUseStart;
                    int toolContentEnd = currentCharIndex - toolCloseTag.Length + 1;

                    // Handle special case for write_to_file / create_file content param
                    if ((currentToolUse.Name == "write_to_file" || currentToolUse.Name == "create_file" ||
                         currentToolUse.Name == "replace_in_file" || currentToolUse.Name == "append_to_file") &&
                        !currentToolUse.Params.ContainsKey("content"))
                    {
                        string toolContentSlice = assistantMessage.Substring(toolContentStart, toolContentEnd - toolContentStart);
                        ExtractContentParam(toolContentSlice, "content", currentToolUse);
                    }

                    currentToolUse.Partial = false;
                    contentBlocks.Add(ParsedContentBlock.CreateToolCall(currentToolUse));

                    // Finalize any trailing text in this tool's content (shouldn't be any normally)
                    currentToolUse = null;
                    currentTextContentStart = currentCharIndex + 1;
                    currentTextContent = null;
                    continue;
                }

                continue; // Still inside tool content
            }

            // --- State: Parsing Text / Looking for Tool Start ---
            if (currentToolUse == null)
            {
                bool startedNewTool = false;

                // Check if starting a new tool use — scan for known tool open tags ending at position i
                if (toolUseOpenTags != null)
                {
                    foreach (var kvp in toolUseOpenTags)
                    {
                        string tag = kvp.Key; // e.g. "<read_files>"
                        string toolName = kvp.Value;
                        if (currentCharIndex >= tag.Length - 1 &&
                            assistantMessage.AsSpan(currentCharIndex - tag.Length + 1, tag.Length).SequenceEqual(tag.AsSpan()))
                        {
                            // End current text block if one was active
                            string textContent = assistantMessage.Substring(currentTextContentStart, currentCharIndex - currentTextContentStart - tag.Length + 1).Trim();
                            if (!string.IsNullOrEmpty(textContent))
                            {
                                contentBlocks.Add(ParsedContentBlock.CreateText(textContent, partial: false));
                            }
                            currentTextContent = null;

                            // Start the new tool use
                            currentToolUse = new ParsedToolCall
                            {
                                Name = toolName,
                                Params = new Dictionary<string, string>(),
                                Partial = true,
                                CallId = $"{toolName}_{Guid.NewGuid().ToString("N")[..8]}"
                            };
                            currentToolUseStart = currentCharIndex + 1; // Content starts after the opening tag
                            startedNewTool = true;
                            break;
                        }
                    }
                }

                if (startedNewTool) continue;

                // Not starting a tool, it's text content
                if (currentTextContent == null)
                {
                    currentTextContentStart = i;
                    currentTextContent = ParsedContentBlock.CreateText("", partial: true);
                }
            }
        }

        // --- Finalization after loop ---

        // Finalize any open parameter within an open tool use
        if (currentToolUse != null && currentParamName != null)
        {
            string value = assistantMessage.Substring(currentParamValueStart).Trim();
            currentToolUse.Params[currentParamName] = value;
            // Tool use remains partial
        }

        // Finalize any open tool use
        if (currentToolUse != null)
        {
            contentBlocks.Add(ParsedContentBlock.CreateToolCall(currentToolUse));
        }
        else if (currentTextContent != null)
        {
            currentTextContent.Text = assistantMessage.Substring(currentTextContentStart).Trim();
            currentTextContent.Partial = true;
            if (currentTextContent.Text.Length > 0)
            {
                contentBlocks.Add(currentTextContent);
            }
        }

        return contentBlocks.ToArray();
    }

    private static bool IsValidParamName(string name)
    {
        // Basic validation: alphanumeric + underscore, not too long
        if (string.IsNullOrEmpty(name) || name.Length > 50) return false;
        if (name.StartsWith("<!")) return false; // Not a valid tag
        if (name.Contains(" ") || name.Contains("/") || name.Contains(">") || name.Contains("<"))
            return false;

        // Reject anything that looks like a self-closing or special tag
        if (name.EndsWith("/>")) return false;

        // Must be a plausible parameter name
        foreach (char c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
                return false;
        }

        return true;
    }

    private static void ExtractContentParam(string toolContentSlice, string paramName, ParsedToolCall toolUse)
    {
        string startTag = $"<{paramName}>";
        string endTag = $"</{paramName}>";

        int contentStart = toolContentSlice.IndexOf(startTag);
        if (contentStart == -1) return;

        // Use lastIndexOf for robustness against nested closing tags
        int contentEnd = toolContentSlice.LastIndexOf(endTag);
        if (contentEnd == -1 || contentEnd <= contentStart) return;

        string contentValue = toolContentSlice.Substring(
            contentStart + startTag.Length,
            contentEnd - contentStart - startTag.Length
        ).Trim();

        toolUse.Params[paramName] = contentValue;
    }

    /// <summary>
    /// Given the full accumulated assistant message text, extract complete
    /// (non-partial) tool calls that can be executed.
    /// Partial blocks are skipped since they represent incomplete streaming data.
    /// </summary>
    public static List<ParsedToolCall> ExtractCompleteToolCalls(string assistantMessage, ISet<string>? knownToolNames = null)
    {
        var blocks = Parse(assistantMessage, knownToolNames);
        var toolCalls = new List<ParsedToolCall>();
        foreach (var block in blocks)
        {
            if (block.IsToolCall && block.ToolCall != null && !block.ToolCall.Partial)
            {
                toolCalls.Add(block.ToolCall);
            }
        }
        return toolCalls;
    }

    /// <summary>
    /// Strip XML-style tool call tags from the text, returning only the prose
    /// (text that exists outside of tool call blocks). This is what gets sent
    /// to the API as the assistant's text-only contribution.
    /// </summary>
    public static string ExtractTextOnly(string assistantMessage, ISet<string>? knownToolNames = null)
    {
        var blocks = Parse(assistantMessage, knownToolNames);
        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            if (block.IsText && !string.IsNullOrEmpty(block.Text))
            {
                sb.Append(block.Text);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Check whether the text contains any unclosed/hallucinated XML that
    /// looks like a tool call attempt but doesn't match known tool names.
    /// </summary>
    public static bool ContainsUnknownXmlTags(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        // Look for <tag> patterns that aren't in the known tool set
        int start = text.IndexOf('<');
        while (start != -1)
        {
            int end = text.IndexOf('>', start);
            if (end == -1) break;

            string tagContent = text.Substring(start, end - start + 1).Trim('<', '>', '/');
            if (!string.IsNullOrEmpty(tagContent) && IsValidParamName(tagContent))
            {
                // This is an unrecognized XML-like tag
                if (char.IsLetter(tagContent[0])) return true;
            }
            start = text.IndexOf('<', end + 1);
        }
        return false;
    }
}
