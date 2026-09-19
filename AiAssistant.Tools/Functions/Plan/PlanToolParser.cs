using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using AiAssistant.Core.Models;

namespace AiAssistant.Tools.Functions.Plan;

/// <summary>
/// Shared parsing helpers for ProposePlanTool and RevisePlanTool.
/// Moves all JSON serialization burden out of the model and into deterministic code.
///
/// Design rationale:
///   The model (weaker free-tier) reliably escapes double quotes at one level (plain string
///   values) but collapses escaping discipline inside JSON array elements. All array parameters
///   are therefore exposed as flat newline-delimited strings; this class reconstructs structure
///   from those flat strings.
///
///   Dual-format support is provided for backward compatibility: the model's conversation
///   history contains old-format calls (JSON arrays), which will arrive at some rate during
///   the transition. Both formats are accepted and distinguished by telemetry counters.
/// </summary>
public static class PlanToolParser
{
    // ── Telemetry counters (thread-safe, zero-allocation increment) ────────────
    // LegacyArrayCount   → dropping to zero signals the dual-format path can be retired.
    // FlatStringCount    → confirms the new schema is being followed.
    // MalformedLineCount → tracks how often the model emits bad file lines.
    // AliasedActionCount → tracks how often action aliases fire vs. exact matches.
    public static int LegacyArrayCount;
    public static int FlatStringCount;
    public static int MalformedLineCount;
    public static int AliasedActionCount;

    // ── Action alias map ───────────────────────────────────────────────────────
    // Accepts natural model variation (e.g. "remove" for Delete, "update" for Modify).
    // Silent defaulting is banned — unmatched values return an error string so the model
    // repairs the field rather than corrupting the approval contract.
    private static readonly Dictionary<string, string> ActionAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["create"] = "Create",
            ["new"]    = "Create",
            ["modify"] = "Modify",
            ["update"] = "Modify",
            ["edit"]   = "Modify",
            ["change"] = "Modify",
            ["delete"] = "Delete",
            ["remove"] = "Delete",
        };

    // ── Bullet / numbering strip regex ────────────────────────────────────────
    // Defensively strips leading "- ", "* ", "1. ", "2) " etc.
    // The schema says "no bullets, no numbering" but a weak model will add them at some rate.
    private static readonly Regex BulletPattern =
        new(@"^(?:[-*•]|\d+[.):])\s+", RegexOptions.Compiled);

    /// <summary>
    /// Parses a flat newline-delimited string (or a legacy JSON array) into a list of strings.
    /// Used for importantChanges, decisions, and risks parameters.
    /// </summary>
    /// <param name="value">JsonElement (String or Array) or null. Array is the legacy format.</param>
    public static List<string> SplitLines(object? value)
    {
        if (value is not JsonElement je)
            return new List<string>();

        // ── Legacy path: model sent a JSON array of strings ──────────────────
        if (je.ValueKind == JsonValueKind.Array)
        {
            Interlocked.Increment(ref LegacyArrayCount);
            return je.EnumerateArray()
                     .Select(e => StripBullet(e.GetString() ?? string.Empty))
                     .Where(s => !string.IsNullOrWhiteSpace(s))
                     .ToList();
        }

        // ── New path: model sent a flat newline-delimited string ─────────────
        if (je.ValueKind == JsonValueKind.String)
        {
            Interlocked.Increment(ref FlatStringCount);
            return StripBulletLines(je.GetString() ?? string.Empty);
        }

        return new List<string>();
    }

    /// <summary>
    /// Parses a flat pipe-delimited string (or a legacy JSON array of objects) into
    /// a list of <see cref="ModifiedFile"/> entries.
    ///
    /// Returns either a <see cref="List{ModifiedFile}"/> (success) or a <see cref="string"/>
    /// (targeted repair instruction for the model). The caller MUST check the return type.
    /// </summary>
    /// <param name="value">JsonElement (String or Array of objects) or null.</param>
    /// <returns>
    /// <see cref="List{ModifiedFile}"/> on success, <see cref="string"/> error message on failure.
    /// </returns>
    public static object ParseFileLines(object? value)
    {
        if (value is not JsonElement je)
            return new List<ModifiedFile>();

        // ── Legacy path: model sent the old array-of-objects format ──────────
        if (je.ValueKind == JsonValueKind.Array)
        {
            Interlocked.Increment(ref LegacyArrayCount);
            var legacyFiles = new List<ModifiedFile>();
            int idx = 0;
            foreach (var item in je.EnumerateArray())
            {
                idx++;
                if (item.ValueKind != JsonValueKind.Object)
                {
                    Interlocked.Increment(ref MalformedLineCount);
                    return BuildFileError(idx,
                        $"array element {idx} is not an object",
                        "Expected an object with 'path', 'action', and 'description' properties.");
                }

                if (!item.TryGetProperty("path", out var pathEl) ||
                    !item.TryGetProperty("action", out var actionEl) ||
                    !item.TryGetProperty("description", out var descEl))
                {
                    Interlocked.Increment(ref MalformedLineCount);
                    return BuildFileError(idx,
                        $"array element {idx} is missing required properties",
                        "Each object must have 'path', 'action', and 'description'.");
                }

                var rawAction = actionEl.GetString() ?? string.Empty;
                if (!ActionAliases.TryGetValue(rawAction, out var normalizedAction))
                {
                    Interlocked.Increment(ref MalformedLineCount);
                    return BuildFileError(idx,
                        $"array element {idx} has unrecognized action '{rawAction}'",
                        "The action must be exactly one of: Create, Modify, or Delete.");
                }

                if (!rawAction.Equals(normalizedAction, StringComparison.Ordinal))
                    Interlocked.Increment(ref AliasedActionCount);

                legacyFiles.Add(new ModifiedFile
                {
                    Path        = pathEl.GetString() ?? string.Empty,
                    Action      = normalizedAction,
                    Description = descEl.GetString() ?? string.Empty,
                });
            }
            return legacyFiles;
        }

        // ── New path: model sent a flat pipe-delimited string ────────────────
        if (je.ValueKind == JsonValueKind.String)
        {
            Interlocked.Increment(ref FlatStringCount);
            var raw = je.GetString() ?? string.Empty;
            var files = new List<ModifiedFile>();
            var lineNumber = 0;

            foreach (var rawLine in raw.Split('\n'))
            {
                lineNumber++;
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                // Split on '|' with maxCount=3 so '|' inside the description is preserved.
                var parts = line.Split(new[] { '|' }, 3);
                if (parts.Length < 3)
                {
                    Interlocked.Increment(ref MalformedLineCount);
                    return BuildFileError(lineNumber,
                        $"line {lineNumber} has {parts.Length} part(s) instead of 3: \"{line}\"",
                        "Expected format: <path> | <action> | <description>. " +
                        "Example: Services/ChatService.cs | Modify | Add retry logic for transient failures");
                }

                var path   = parts[0].Trim();
                var rawAct = parts[1].Trim();
                var desc   = parts[2].Trim();

                if (!ActionAliases.TryGetValue(rawAct, out var normalizedAction))
                {
                    Interlocked.Increment(ref MalformedLineCount);
                    return BuildFileError(lineNumber,
                        $"line {lineNumber} has unrecognized action '{rawAct}'",
                        "The action must be exactly one of: Create, Modify, or Delete. " +
                        "Example: Services/ChatService.cs | Modify | Add retry logic for transient failures");
                }

                if (!rawAct.Equals(normalizedAction, StringComparison.Ordinal))
                    Interlocked.Increment(ref AliasedActionCount);

                files.Add(new ModifiedFile
                {
                    Path        = path,
                    Action      = normalizedAction,
                    Description = desc,
                });
            }
            return files;
        }

        return new List<ModifiedFile>();
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static string StripBullet(string line) =>
        BulletPattern.Replace(line.Trim(), string.Empty);

    private static List<string> StripBulletLines(string raw) =>
        raw.Split('\n')
           .Select(l => StripBullet(l))
           .Where(l => !string.IsNullOrWhiteSpace(l))
           .ToList();

    /// <summary>
    /// Builds the targeted repair instruction returned to the model on a parse failure.
    /// The "keep all other parameters exactly the same" clause is the drift anchor —
    /// it prevents content regeneration (blue-gradient vs. white-scheme drift observed
    /// in the original failure log) when only the 'files' field needs fixing.
    /// </summary>
    private static string BuildFileError(int lineOrIndex, string problem, string hint) =>
        $"Error in 'files': {problem}. {hint} " +
        "Re-call propose_plan keeping all other parameters exactly the same, " +
        "fixing only the 'files' parameter.";
}


