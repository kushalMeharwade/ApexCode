using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Services;
using AiAssistant.Tools.Services;
using Microsoft.Extensions.AI;

namespace AiAssistant.Tools.Functions;

/// <summary>
/// Reads one or more files from the workspace with per-file line ranges.
///
/// Schema change rationale:
///   The old signature (string[] filePaths, int startLine, int endLine) was internally
///   incoherent: a single startLine/endLine pair applies identically to every file in the
///   batch, making independent ranges structurally impossible. The model learned this and
///   reflexively passed exactly one path per call, even where batching would be natural.
///   The new schema takes an array of { path, startLine?, endLine? } objects — each file
///   carries its own optional range. The old endLine=0 sentinel is replaced by simply
///   omitting endLine (self-documenting optional).
///
///   Array-of-objects is appropriate here (unlike the prose-array failure in propose_plan)
///   because the values are short path strings and integers — no free-text prose that would
///   trigger the model's quote-escaping failure mode.
/// </summary>
public class ReadFileLinesFunction : CustomAIFunction
{
    private readonly IApprovalService? _approvalService;
    private readonly IVisualStudioEnvironmentService _vsEnvService;
    private readonly IOutputLogger _outputLogger;
    private readonly ISettingsService _settingsService;
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10MB

    // Cache: absolutePath -> (LastWriteTime, Content)
    private static readonly ConcurrentDictionary<string, (DateTime LastWrite, string Content)> _fileCache = new();

    private readonly IFileReadTracker _fileReadTracker;

    // Telemetry: LegacyCallCount dropping to zero signals the legacy fallback can be removed.
    public static int LegacyCallCount;

    public ReadFileLinesFunction(
        IVisualStudioEnvironmentService vsEnvService,
        ISettingsService settingsService,
        IOutputLogger outputLogger,
        IFileReadTracker fileReadTracker,
        IApprovalService? approvalService = null)
        : base(
            name: "read_files",
            description: "Reads the content of one or more files from the workspace. Each file can specify its own optional line range. Automatically paginates if the requested range exceeds 300 lines. Path must be relative to workspace root with forward slashes.",
            jsonSchemaString: """
            {
                "type": "object",
                "properties": {
                    "files": {
                        "type": "array",
                        "description": "List of files to read. Each entry specifies a file path and an optional line range. Omit startLine/endLine to read the entire file.",
                        "items": {
                            "type": "object",
                            "properties": {
                                "path": {
                                    "type": "string",
                                    "description": "Path to the file, relative to workspace root. Use forward slashes. Example: src/app/pages/file.html. Cannot be used to read out-of-root linked files (files outside the workspace root, including csproj <Link> items that resolve outside the root)."
                                },
                                "startLine": {
                                    "type": "integer",
                                    "description": "1-indexed first line to read (inclusive). Omit to start from line 1."
                                },
                                "endLine": {
                                    "type": "integer",
                                    "description": "1-indexed last line to read (inclusive). Omit to read to the end of the file."
                                }
                            },
                            "required": ["path"]
                        },
                        "minItems": 1
                    }
                },
                "required": ["files"]
            }
            """)
    {
        _vsEnvService = vsEnvService;
        _settingsService = settingsService;
        _outputLogger = outputLogger;
        _fileReadTracker = fileReadTracker;
        _approvalService = approvalService;
    }

    protected override async Task<object?> InvokeCoreImplAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        // ── Build the per-file request list ───────────────────────────────────
        // Supports two formats for backward compatibility:
        //   New: { "files": [{ "path": "...", "startLine": 1, "endLine": 50 }, ...] }
        //   Legacy: { "filePaths": ["..."], "startLine": 1, "endLine": 0 }
        var fileRequests = new List<(string Path, int StartLine, int? EndLine)>();

        if (arguments.TryGetValue("files", out var filesVal) &&
            filesVal is JsonElement filesEl &&
            filesEl.ValueKind == JsonValueKind.Array)
        {
            // ── New per-file-range format ──────────────────────────────────
            foreach (var entry in filesEl.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("path", out var pathEl)) continue;

                var path = pathEl.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(path)) continue;

                int startLine = 1;
                int? endLine = null;

                if (entry.TryGetProperty("startLine", out var startEl) || 
                    entry.TryGetProperty("start", out startEl) || 
                    entry.TryGetProperty("lineStart", out startEl))
                {
                    if (startEl.ValueKind == JsonValueKind.Number)
                        startLine = startEl.GetInt32();
                }

                if (entry.TryGetProperty("endLine", out var endEl) || 
                    entry.TryGetProperty("end", out endEl) || 
                    entry.TryGetProperty("lineEnd", out endEl))
                {
                    if (endEl.ValueKind == JsonValueKind.Number)
                        endLine = endEl.GetInt32();
                }

                fileRequests.Add((path, startLine, endLine));
            }
        }
        else if (arguments.TryGetValue("filePaths", out var legacyPathsVal) &&
                 legacyPathsVal is JsonElement legacyEl &&
                 legacyEl.ValueKind == JsonValueKind.Array)
        {
            // ── Legacy flat-parameter fallback ────────────────────────────
            Interlocked.Increment(ref LegacyCallCount);
            _outputLogger.Log(LogCategory.Tool, "[ReadFileTool] Legacy 'filePaths' format detected — consider upgrading to per-file 'files' array.");

            int legacyStart = 1;
            int? legacyEnd = null;

            if (arguments.TryGetValue("startLine", out var startVal) && startVal is JsonElement startEl2 && startEl2.ValueKind == JsonValueKind.Number)
                legacyStart = startEl2.GetInt32();

            if (arguments.TryGetValue("endLine", out var endVal) && endVal is JsonElement endEl2 && endEl2.ValueKind == JsonValueKind.Number)
            {
                var raw = endEl2.GetInt32();
                legacyEnd = raw == 0 ? null : raw; // treat old sentinel 0 as "read to end" (= null)
            }

            foreach (var pe in legacyEl.EnumerateArray())
            {
                var p = pe.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(p))
                    fileRequests.Add((p, legacyStart, legacyEnd));
            }
        }

        if (fileRequests.Count == 0)
        {
            _outputLogger.Log(LogCategory.Tool, "◄ read_files → Error: no valid file paths provided.");
            return JsonSerializer.Serialize(new { error = "No valid file paths provided. Supply a 'files' array with at least one entry containing a 'path' property." });
        }

        // ── Log the request ───────────────────────────────────────────────────
        var summary = string.Join(", ", fileRequests.Select(r =>
            r.EndLine.HasValue
                ? $"{r.Path} (lines {r.StartLine}–{r.EndLine})"
                : r.StartLine > 1
                    ? $"{r.Path} (lines {r.StartLine}–end)"
                    : $"{r.Path} (all lines)"));
        _outputLogger.Log(LogCategory.Tool, $"► read_files({summary})");

        // ── Approval check ────────────────────────────────────────────────────
        var approvalMode = _settingsService.GetToolApprovalMode("read_files");
        if (approvalMode == "denyall")
        {
            _outputLogger.Log(LogCategory.Tool, "◄ read_files → Error: Tool execution rejected due to Deny All setting.");
            return JsonSerializer.Serialize(new { error = "User rejected the request to read files." });
        }

        if (_approvalService != null && approvalMode != "allowall")
        {
            // Build a human-readable per-file summary for the approval dialog.
            var dialogLines = fileRequests.Select(r =>
                r.EndLine.HasValue
                    ? $"  {r.Path} (lines {r.StartLine}–{r.EndLine})"
                    : $"  {r.Path} (all lines)");
            var dialogText = $"Read {fileRequests.Count} file(s):\n{string.Join("\n", dialogLines)}";

            var approvalRequest = await _approvalService.RequestApprovalAsync(
                "read_files",
                dialogText,
                new Dictionary<string, object?> { { "files", fileRequests.Select(r => r.Path).ToArray() } });

            await foreach (var status in _approvalService.WaitForApprovalAsync(approvalRequest.Id, cancellationToken))
            {
                if (status.IsRejected)
                {
                    _outputLogger.Log(LogCategory.Tool, "◄ read_files → Error: User rejected the request to read files.");
                    return "USER REJECTED THIS ACTION. CRITICAL INSTRUCTION: Do NOT retry this tool. Do NOT attempt alternative commands. You MUST STOP and ask the user for further instructions.";
                }
                if (status.IsApproved) break;
            }
        }

        // ── Resolve workspace root ────────────────────────────────────────────
        var workspacePath = await _vsEnvService.GetWorkspaceRootAsync(cancellationToken);
        if (string.IsNullOrEmpty(workspacePath))
            return JsonSerializer.Serialize(new { error = "No workspace is open." });

        var workspaceRoot = Path.GetFullPath(workspacePath);

        // ── Read each file ────────────────────────────────────────────────────
        var results = new List<string>();

        foreach (var (filePath, startLine, endLine) in fileRequests)
        {
            string absolutePath;
            try
            {
                absolutePath = WorkspacePathResolver.ResolveWorkspacePath(filePath, workspaceRoot);
            }
            catch (Exception ex)
            {
                _outputLogger.Log($"[ReadFileTool] SECURITY WARNING or BAD PATH: {ex.Message}");
                results.Add(JsonSerializer.Serialize(new { error = $"Path resolution failed or traversal denied for: {filePath}. {ex.Message}" }));
                continue;
            }

            string relativePathOut = WorkspacePathResolver.ToRelativePath(absolutePath, workspaceRoot);

            _outputLogger.Log($"[ReadFileTool] Request: '{filePath}' -> '{absolutePath}'");

            if (!File.Exists(absolutePath))
            {
                _outputLogger.Log($"[ReadFileTool] Error: File not found ({absolutePath})");
                results.Add(JsonSerializer.Serialize(new { error = $"File not found. It may have been deleted or moved. Path: {relativePathOut}" }));
                continue;
            }

            var fileInfo = new FileInfo(absolutePath);
            if (fileInfo.Length > MaxFileSizeBytes)
            {
                results.Add(JsonSerializer.Serialize(new { error = $"File too large: {relativePathOut} ({fileInfo.Length / (1024 * 1024)} MB, max {MaxFileSizeBytes / (1024 * 1024)} MB)" }));
                continue;
            }

            try
            {
                // Cache by LastWriteTime
                var lastWrite = File.GetLastWriteTimeUtc(absolutePath);
                string content;

                if (_fileCache.TryGetValue(absolutePath, out var cached) && cached.LastWrite == lastWrite)
                {
                    _outputLogger.Log($"[ReadFileTool] Cache hit: {absolutePath}");
                    content = cached.Content;
                }
                else
                {
                    _outputLogger.Log($"[ReadFileTool] Reading from disk: {absolutePath}");
                    content = await Task.Run(() => File.ReadAllText(absolutePath));
                    _fileCache[absolutePath] = (lastWrite, content);
                }

                _fileReadTracker.MarkFileRead(absolutePath, DateTime.UtcNow);

                var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                var totalLines = lines.Length;

                // Resolve optional range — absent endLine means "read to end"
                int actualStart = Math.Max(1, startLine);
                int resolvedEnd = endLine.HasValue ? Math.Min(endLine.Value, totalLines) : totalLines;

                if (actualStart > resolvedEnd && resolvedEnd != 0)
                {
                    results.Add(JsonSerializer.Serialize(new { error = $"Validation failed for {relativePathOut}: startLine ({actualStart}) cannot be greater than endLine ({resolvedEnd})." }));
                    continue;
                }

                if (actualStart > totalLines)
                {
                    results.Add($"File: {relativePathOut}\nTotal Lines: {totalLines}\n```\n\n```\n[Warning: startLine ({actualStart}) is past the end of the file]");
                    continue;
                }

                const int MaxAllowedLines = 300;
                int count = resolvedEnd - actualStart + 1;
                bool wasTruncated = false;

                if (count > MaxAllowedLines)
                {
                    count = MaxAllowedLines;
                    resolvedEnd = actualStart + count - 1;
                    wasTruncated = true;
                }

                var selectedLines = lines.Skip(actualStart - 1).Take(count);
                var slicedContent = string.Join("\n", selectedLines);

                var msg = $"File: {relativePathOut}\nTotal Lines: {totalLines}\nShowing Lines: {actualStart}-{resolvedEnd}\n```\n{slicedContent}\n```";

                if (wasTruncated)
                {
                    var originalEnd = endLine.HasValue ? endLine.Value : totalLines;
                    msg += $"\n\nReturned lines {actualStart}–{resolvedEnd} of the requested {actualStart}–{originalEnd}; call again with startLine={resolvedEnd + 1} for the next range.";
                }

                results.Add(msg);
                _outputLogger.Log($"[ReadFileLinesTool] Read success: {absolutePath} ({slicedContent.Length} chars)");
            }
            catch (Exception ex)
            {
                _outputLogger.Log($"[ReadFileTool] ERROR: {ex.Message}");
                results.Add(JsonSerializer.Serialize(new { error = $"Error reading file {relativePathOut}: {ex.Message}" }));
            }
        }

        _outputLogger.Log(LogCategory.Tool, $"◄ read_files → Processed {fileRequests.Count} file(s)");
        return string.Join("\n\n", results);
    }

    private bool IsPathSafe(string fullPath, string workspaceRoot) =>
        PathJail.IsContained(fullPath, workspaceRoot);

    /// <summary>
    /// Legacy entry point kept for backward compatibility.
    /// New code should register ReadFileLinesFunction as a CustomAIFunction (via DI) and
    /// pass a 'files' array. This method is retained only to prevent breaking existing
    /// test harnesses that call it directly.
    /// </summary>
    [Obsolete("Use the CustomAIFunction schema with the 'files' per-file-range array. This method will be removed when LegacyCallCount reaches zero.")]
    public async Task<string> ReadFileLinesAsync(
        [Description("The relative or absolute path to the file to read. Can be a single file or array of files.")] string[] filePaths,
        [Description("The 1-indexed starting line to read. Default is 1.")] int startLine = 1,
        [Description("The 1-indexed ending line to read (inclusive). 0 means read to the end of the file.")] int endLine = 0,
        CancellationToken cancellationToken = default)
    {
        // Reconstruct the new-format argument dict and delegate to the unified impl.
        var entries = filePaths?.Where(p => !string.IsNullOrWhiteSpace(p))
                                .Select(p => (p.Trim(), startLine, endLine == 0 ? (int?)null : endLine))
                                .ToList() ?? new List<(string, int, int?)>();

        if (entries.Count == 0)
            return JsonSerializer.Serialize(new { error = "filePaths argument is missing or empty." });

        // Build a synthetic JsonElement-based argument dict isn't practical here, so
        // call the shared per-file read logic directly via the internal overload.
        var results = new List<string>();
        var workspacePath = await _vsEnvService.GetWorkspaceRootAsync(cancellationToken);
        if (string.IsNullOrEmpty(workspacePath))
            return JsonSerializer.Serialize(new { error = "No workspace is open." });
        var workspaceRoot = Path.GetFullPath(workspacePath);

        foreach (var (path, sl, el) in entries)
        {
            // Reuse the core logic inline — this avoids duplicating approval checks
            // (legacy callers are assumed to have already handled approval).
            string absPath;
            try
            {
                absPath = WorkspacePathResolver.ResolveWorkspacePath(path, workspaceRoot);
            }
            catch (Exception ex)
            {
                results.Add(JsonSerializer.Serialize(new { error = $"Path traversal denied or resolution failed: {path}. {ex.Message}" }));
                continue;
            }

            string relativePathOut = WorkspacePathResolver.ToRelativePath(absPath, workspaceRoot);

            if (!File.Exists(absPath))
            { results.Add(JsonSerializer.Serialize(new { error = $"File not found: {relativePathOut}" })); continue; }

            var lastWrite = File.GetLastWriteTimeUtc(absPath);
            string content;
            if (_fileCache.TryGetValue(absPath, out var c) && c.LastWrite == lastWrite)
                content = c.Content;
            else
            {
                content = await Task.Run(() => File.ReadAllText(absPath));
                _fileCache[absPath] = (lastWrite, content);
            }
            _fileReadTracker.MarkFileRead(absPath, DateTime.UtcNow);

            var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var totalLines = lines.Length;
            int actualStart = Math.Max(1, sl);
            int resolvedEnd = el.HasValue ? Math.Min(el.Value, totalLines) : totalLines;

            const int Max = 300;
            int count = resolvedEnd - actualStart + 1;
            bool truncated = false;
            if (count > Max) { count = Max; resolvedEnd = actualStart + count - 1; truncated = true; }

            var sliced = string.Join("\n", lines.Skip(actualStart - 1).Take(count));
            var msg = $"File: {relativePathOut}\nTotal Lines: {totalLines}\nShowing Lines: {actualStart}-{resolvedEnd}\n```\n{sliced}\n```";
            if (truncated) msg += $"\n\nReturned lines {actualStart}–{resolvedEnd}; call again with startLine={resolvedEnd + 1} for more.";
            results.Add(msg);
        }

        return string.Join("\n\n", results);
    }
}



