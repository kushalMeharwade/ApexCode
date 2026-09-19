using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Tools.Services;
using AiAssistant.Core.Services;

namespace AiAssistant.Tools.Functions;

public class SingleOrArrayConverter<T> : JsonConverter<List<T>>
{
    public override List<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            using (var doc = JsonDocument.ParseValue(ref reader))
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    try
                    {
                        var array = JsonSerializer.Deserialize<T[]>(doc.RootElement.GetRawText(), options);
                        return array != null ? new List<T>(array) : new List<T>();
                    }
                    catch (JsonException ex)
                    {
                        throw new JsonException($"Failed to deserialize array of {typeof(T).Name}: {ex.Message}. JSON: {TruncateJson(doc.RootElement.GetRawText())}", ex);
                    }
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.String)
                {
                    var strVal = doc.RootElement.GetString();
                    if (!string.IsNullOrWhiteSpace(strVal) && (strVal.TrimStart().StartsWith("[") || strVal.TrimStart().StartsWith("{")))
                    {
                        try 
                        {
                            using var innerDoc = JsonDocument.Parse(strVal);
                            if (innerDoc.RootElement.ValueKind == JsonValueKind.Array)
                            {
                                var array = JsonSerializer.Deserialize<T[]>(innerDoc.RootElement.GetRawText(), options);
                                return array != null ? new List<T>(array) : new List<T>();
                            }
                            else
                            {
                                var singleItem = JsonSerializer.Deserialize<T>(innerDoc.RootElement.GetRawText(), options);
                                return singleItem != null ? new List<T> { singleItem } : new List<T>();
                            }
                        }
                        catch (JsonException ex)
                        {
                            throw new JsonException($"Failed to parse string as {typeof(T).Name}: {ex.Message}. String content: {TruncateJson(strVal)}", ex);
                        }
                    }
                }

                try
                {
                    var fallbackItem = JsonSerializer.Deserialize<T>(doc.RootElement.GetRawText(), options);
                    return fallbackItem != null ? new List<T> { fallbackItem } : new List<T>();
                }
                catch (JsonException ex)
                {
                    throw new JsonException($"Failed to deserialize as {typeof(T).Name}. Expected an array or object with required properties. JSON: {TruncateJson(doc.RootElement.GetRawText())}. Error: {ex.Message}", ex);
                }
            }
        }
        catch (JsonException)
        {
            // Re-throw JsonException as-is to preserve our custom messages
            throw;
        }
        catch (Exception ex)
        {
            throw new JsonException($"Unexpected error in SingleOrArrayConverter<{typeof(T).Name}>: {ex.Message}", ex);
        }
    }

    private static string TruncateJson(string json, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(json) || json.Length <= maxLength)
            return json;
        return json.Substring(0, maxLength) + "... (truncated)";
    }

    public override void Write(Utf8JsonWriter writer, List<T> value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, options);
    }
}

public class ReplaceTransactionRequest
{
    [JsonPropertyName("dryRun")]
    public bool DryRun { get; set; }

    [JsonPropertyName("files")]
    [JsonConverter(typeof(SingleOrArrayConverter<ReplaceFileRequest>))]
    public List<ReplaceFileRequest> Files { get; set; } = new();

    // Fallback for single-file, single-edit
    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("edits")]
    [JsonConverter(typeof(SingleOrArrayConverter<ReplaceEditRequest>))]
    public List<ReplaceEditRequest>? Edits { get; set; }

    [JsonPropertyName("oldText")]
    public string? OldText { get; set; }

    [JsonPropertyName("newText")]
    public string? NewText { get; set; }
    
    [JsonPropertyName("expectedOccurrences")]
    public int? ExpectedOccurrences { get; set; }
}

public class ReplaceFileRequest
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = "";

    [JsonPropertyName("edits")]
    [JsonConverter(typeof(SingleOrArrayConverter<ReplaceEditRequest>))]
    public List<ReplaceEditRequest> Edits { get; set; } = new();
}

public class ReplaceEditRequest
{
    [JsonPropertyName("oldText")]
    public string OldText { get; set; } = "";

    [JsonPropertyName("newText")]
    public string NewText { get; set; } = "";

    [JsonPropertyName("expectedOccurrences")]
    public int? ExpectedOccurrences { get; set; }
}

public class ReplaceFileFunction : IToolProvider
{
    private readonly IApprovalService? _approvalService;
    private readonly IOutputLogger? _outputLogger;
    private readonly ISettingsService _settingsService;
    private readonly IVisualStudioEnvironmentService _vsEnvService;
    private readonly ITransactionSnapshotService _transactionService;
    private readonly IFileReadTracker _fileReadTracker;

    public ReplaceFileFunction(
        ISettingsService settingsService,
        IVisualStudioEnvironmentService vsEnvService,
        ITransactionSnapshotService transactionService,
        IFileReadTracker fileReadTracker,
        IApprovalService? approvalService = null,
        IOutputLogger? outputLogger = null)
    {
        _settingsService = settingsService;
        _vsEnvService = vsEnvService;
        _transactionService = transactionService;
        _fileReadTracker = fileReadTracker;
        _approvalService = approvalService;
        _outputLogger = outputLogger;
    }

    public delegate Task<bool> PreviewResolverDelegate(Dictionary<string, string> originalContents, Dictionary<string, string> newContents);
    public static PreviewResolverDelegate? PreviewResolver { get; set; }

    [Description("Replaces content in one or more files transactionally with new content. Supports multiple edits per file.")]
    public AIFunction CreateFunction() => new ReplaceFileCustomFunction(_settingsService, _vsEnvService, _transactionService, _fileReadTracker, _approvalService, _outputLogger);

    private class ReplaceFileCustomFunction : CustomAIFunction
    {
        private readonly ISettingsService _settingsService;
        private readonly IVisualStudioEnvironmentService _vsEnvService;
        private readonly ITransactionSnapshotService _transactionService;
        private readonly IFileReadTracker _fileReadTracker;
        private readonly IApprovalService? _approvalService;
        private readonly IOutputLogger? _outputLogger;

        public ReplaceFileCustomFunction(
            ISettingsService settingsService,
            IVisualStudioEnvironmentService vsEnvService,
            ITransactionSnapshotService transactionService,
            IFileReadTracker fileReadTracker,
            IApprovalService? approvalService,
            IOutputLogger? outputLogger)
            : base("replace_in_file",
                   "Replaces content in one or more files transactionally with new content. Supports multiple edits per file. Use this for applying code changes.",
                   @"{
                        ""type"": ""object"",
                        ""properties"": {
                            ""dryRun"": { ""type"": ""boolean"", ""description"": ""Optional. Default false. If true, returns diffs for all files without writing any."" },
                            ""files"": {
                                ""type"": ""array"",
                                ""items"": {
                                    ""type"": ""object"",
                                    ""properties"": {
                                         ""filePath"": { ""type"": ""string"", ""description"": ""The path to the file, relative to workspace root. Use forward slashes. Example: src/app/pages/file.html. Cannot be used to edit out-of-root linked files (files outside the workspace root, including csproj <Link> items that resolve outside the root)."" },
                                        ""edits"": {
                                            ""type"": ""array"",
                                            ""items"": {
                                                ""type"": ""object"",
                                                ""properties"": {
                                                    ""oldText"": { ""type"": ""string"", ""description"": ""The exact-ish text to replace, must be unique in file."" },
                                                    ""newText"": { ""type"": ""string"", ""description"": ""The new text to insert."" },
                                                    ""expectedOccurrences"": { ""type"": ""integer"", ""description"": ""Optional. Default 1. CRITICAL: Set this explicitly if the target text might appear multiple times. If expectedOccurrences > 1, all matches are replaced identically with newText; for non-uniform edits, use separate entries in edits[] with more specific oldText."" }
                                                },
                                                ""required"": [""oldText"", ""newText""]
                                            }
                                        }
                                    },
                                    ""required"": [""filePath"", ""edits""]
                                }
                            },
                             ""filePath"": { ""type"": ""string"", ""description"": ""Fallback: for a single file edit, the path to the file relative to workspace root. Cannot be used to edit out-of-root linked files (files outside the workspace root, including csproj <Link> items that resolve outside the root)."" },
                            ""edits"": {
                                ""type"": ""array"",
                                ""description"": ""Fallback: for a single file edit, the array of edits."",
                                ""items"": {
                                    ""type"": ""object"",
                                    ""properties"": {
                                        ""oldText"": { ""type"": ""string"" },
                                        ""newText"": { ""type"": ""string"" }
                                    },
                                    ""required"": [""oldText"", ""newText""]
                                }
                            },
                            ""oldText"": { ""type"": ""string"", ""description"": ""Fallback: for a single file, single edit, the text to replace."" },
                            ""newText"": { ""type"": ""string"", ""description"": ""Fallback: for a single file, single edit, the new text."" }
                        }
                   }")
        {
            _settingsService = settingsService;
            _vsEnvService = vsEnvService;
            _transactionService = transactionService;
            _fileReadTracker = fileReadTracker;
            _approvalService = approvalService;
            _outputLogger = outputLogger;
        }

        private string? ValidateRequestSchema(string jsonRequest)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonRequest);
                var root = doc.RootElement;

                // Check if we have any valid structure
                bool hasFilesArray = root.TryGetProperty("files", out var filesProperty);
                bool hasFallbackFilePath = root.TryGetProperty("filePath", out var filePathProperty);

                if (!hasFilesArray && !hasFallbackFilePath)
                {
                    return "Request must contain either 'files' array or 'filePath' property.";
                }

                // Validate files array if present
                if (hasFilesArray)
                {
                    if (filesProperty.ValueKind != JsonValueKind.Array && filesProperty.ValueKind != JsonValueKind.String)
                    {
                        return "'files' must be an array or a string representation of an array.";
                    }

                    JsonElement filesArray;
                    if (filesProperty.ValueKind == JsonValueKind.String)
                    {
                        try
                        {
                            filesArray = JsonDocument.Parse(filesProperty.GetString() ?? "[]").RootElement;
                        }
                        catch
                        {
                            return "'files' string could not be parsed as JSON.";
                        }
                    }
                    else
                    {
                        filesArray = filesProperty;
                    }

                    if (filesArray.ValueKind == JsonValueKind.Array)
                    {
                        int fileIndex = 0;
                        foreach (var file in filesArray.EnumerateArray())
                        {
                            var fileError = ValidateFileObject(file, fileIndex);
                            if (fileError != null)
                            {
                                return fileError;
                            }
                            fileIndex++;
                        }
                    }
                }

                // Validate fallback structure
                if (hasFallbackFilePath)
                {
                    if (filePathProperty.ValueKind != JsonValueKind.String)
                    {
                        return "'filePath' must be a string.";
                    }

                    // Check for edits array at root level
                    if (root.TryGetProperty("edits", out var editsProperty))
                    {
                        var editsError = ValidateEditsProperty(editsProperty, "root level");
                        if (editsError != null)
                        {
                            return editsError;
                        }
                    }
                    // Or check for oldText/newText at root level
                    else if (root.TryGetProperty("oldText", out var oldTextProperty))
                    {
                        if (oldTextProperty.ValueKind != JsonValueKind.String)
                        {
                            return "'oldText' must be a string.";
                        }
                        if (!root.TryGetProperty("newText", out var newTextProperty))
                        {
                            return "When 'oldText' is provided, 'newText' is required.";
                        }
                        if (newTextProperty.ValueKind != JsonValueKind.String)
                        {
                            return "'newText' must be a string.";
                        }
                    }
                    else
                    {
                        return "When 'filePath' is provided, either 'edits' array or 'oldText'/'newText' pair is required.";
                    }
                }

                return null; // Validation passed
            }
            catch (JsonException ex)
            {
                return $"Invalid JSON structure: {ex.Message}";
            }
            catch (Exception ex)
            {
                return $"Validation error: {ex.Message}";
            }
        }

        private string? ValidateFileObject(JsonElement file, int fileIndex)
        {
            if (file.ValueKind != JsonValueKind.Object)
            {
                return $"File at index {fileIndex} must be an object.";
            }

            if (!file.TryGetProperty("filePath", out var filePath))
            {
                return $"File at index {fileIndex} is missing required 'filePath' property.";
            }

            if (filePath.ValueKind != JsonValueKind.String)
            {
                return $"File at index {fileIndex}: 'filePath' must be a string.";
            }

            if (!file.TryGetProperty("edits", out var edits))
            {
                return $"File at index {fileIndex} is missing required 'edits' property.";
            }

            return ValidateEditsProperty(edits, $"file at index {fileIndex}");
        }

        private string? ValidateEditsProperty(JsonElement editsProperty, string context)
        {
            if (editsProperty.ValueKind != JsonValueKind.Array && editsProperty.ValueKind != JsonValueKind.String)
            {
                return $"'edits' in {context} must be an array or a string representation of an array.";
            }

            JsonElement editsArray;
            if (editsProperty.ValueKind == JsonValueKind.String)
            {
                try
                {
                    var editsString = editsProperty.GetString();
                    if (string.IsNullOrWhiteSpace(editsString))
                    {
                        return $"'edits' string in {context} is empty.";
                    }
                    editsArray = JsonDocument.Parse(editsString).RootElement;
                }
                catch (JsonException)
                {
                    return $"'edits' string in {context} could not be parsed as JSON.";
                }
            }
            else
            {
                editsArray = editsProperty;
            }

            if (editsArray.ValueKind == JsonValueKind.Array)
            {
                if (editsArray.GetArrayLength() == 0)
                {
                    return $"'edits' array in {context} cannot be empty.";
                }

                int editIndex = 0;
                foreach (var edit in editsArray.EnumerateArray())
                {
                    var editError = ValidateEditObject(edit, editIndex, context);
                    if (editError != null)
                    {
                        return editError;
                    }
                    editIndex++;
                }
            }
            else if (editsArray.ValueKind == JsonValueKind.Object)
            {
                // Single edit object (will be converted to array by SingleOrArrayConverter)
                return ValidateEditObject(editsArray, 0, context);
            }
            else
            {
                return $"'edits' in {context} must be an array or object, got {editsArray.ValueKind}.";
            }

            return null;
        }

        private string? ValidateEditObject(JsonElement edit, int editIndex, string context)
        {
            if (edit.ValueKind != JsonValueKind.Object)
            {
                return $"Edit at index {editIndex} in {context} must be an object, got {edit.ValueKind}.";
            }

            if (!edit.TryGetProperty("oldText", out var oldText))
            {
                return $"Edit at index {editIndex} in {context} is missing required 'oldText' property.";
            }

            if (oldText.ValueKind != JsonValueKind.String)
            {
                return $"Edit at index {editIndex} in {context}: 'oldText' must be a string, got {oldText.ValueKind}.";
            }

            if (!edit.TryGetProperty("newText", out var newText))
            {
                return $"Edit at index {editIndex} in {context} is missing required 'newText' property.";
            }

            if (newText.ValueKind != JsonValueKind.String)
            {
                return $"Edit at index {editIndex} in {context}: 'newText' must be a string, got {newText.ValueKind}.";
            }

            // Validate expectedOccurrences if present
            if (edit.TryGetProperty("expectedOccurrences", out var expectedOccurrences))
            {
                if (expectedOccurrences.ValueKind != JsonValueKind.Number)
                {
                    return $"Edit at index {editIndex} in {context}: 'expectedOccurrences' must be a number, got {expectedOccurrences.ValueKind}.";
                }

                if (!expectedOccurrences.TryGetInt32(out var occurrences) || occurrences < 1)
                {
                    return $"Edit at index {editIndex} in {context}: 'expectedOccurrences' must be a positive integer.";
                }
            }

            return null;
        }

        protected override async Task<object?> InvokeCoreImplAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
        {
            try
            {
                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var reqJson = JsonSerializer.Serialize(arguments);
                
                // Schema validation before deserialization
                var validationError = ValidateRequestSchema(reqJson);
                if (validationError != null)
                {
                    _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → Schema Validation Error: {validationError}");
                    return $"Schema Validation Error: {validationError}";
                }
                
                var request = JsonSerializer.Deserialize<ReplaceTransactionRequest>(reqJson, jsonOptions);

                if (request != null)
                {
                    request.Files ??= new List<ReplaceFileRequest>();

                    // Fallback 1: filePath + edits array (at root)
                    if (request.Files.Count == 0 && !string.IsNullOrEmpty(request.FilePath) && request.Edits != null && request.Edits.Count > 0)
                    {
                        request.Files.Add(new ReplaceFileRequest
                        {
                            FilePath = request.FilePath,
                            Edits = request.Edits
                        });
                    }
                    // Fallback 2: filePath + oldText + newText (at root)
                    else if (request.Files.Count == 0 && !string.IsNullOrEmpty(request.FilePath) && !string.IsNullOrEmpty(request.OldText))
                    {
                        request.Files.Add(new ReplaceFileRequest
                        {
                            FilePath = request.FilePath,
                            Edits = new List<ReplaceEditRequest>
                            {
                                new ReplaceEditRequest
                                {
                                    OldText = request.OldText,
                                    NewText = request.NewText ?? "",
                                    ExpectedOccurrences = request.ExpectedOccurrences
                                }
                            }
                        });
                    }
                }

                if (request == null || request.Files == null || request.Files.Count == 0)
                {
                    return "Error: Request must contain at least one file with edits. Use the 'files' array, or provide 'filePath' and 'edits' directly.";
                }

                _outputLogger?.Log(LogCategory.Tool, $"► {Name}({request.Files.Count} files, DryRun={request.DryRun})");

                var approvalMode = _settingsService.GetToolApprovalMode("replace_in_file");
                if (approvalMode == "denyall")
                {
                    _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → Error: Tool execution rejected due to Deny All setting.");
                    return "Error: User rejected tool execution.";
                }

                if (_approvalService != null && approvalMode != "allowall")
                {
                    var req = await _approvalService.RequestApprovalAsync(Name, $"Replace in {request.Files.Count} file(s)", arguments.ToDictionary(k => k.Key, v => (object?)v.Value));
                    await foreach (var status in _approvalService.WaitForApprovalAsync(req.Id, cancellationToken))
                    {
                        if (status.IsRejected)
                        {
                            _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → Error: User rejected tool execution.");
                            return "USER REJECTED THIS ACTION. CRITICAL INSTRUCTION: Do NOT retry this tool. Do NOT attempt alternative commands. You MUST STOP and ask the user for further instructions.";
                        }
                        if (status.IsApproved) break;
                    }
                }

                // Path Jail validation
                var workspaceRoot = await _vsEnvService.GetWorkspaceRootAsync(cancellationToken);
                if (string.IsNullOrEmpty(workspaceRoot))
                {
                    return "Error: No workspace is open.";
                }

                foreach (var fileReq in request.Files)
                {
                    string absoluteFilePath;
                    try
                    {
                        absoluteFilePath = WorkspacePathResolver.ResolveWorkspacePath(fileReq.FilePath, workspaceRoot);
                    }
                    catch (Exception ex)
                    {
                        var jailMsg = $"Error: Path resolution failed or traversal denied for '{fileReq.FilePath}'. {ex.Message}";
                        _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → {jailMsg}");
                        return jailMsg;
                    }

                    fileReq.FilePath = absoluteFilePath;
                }
                
                return await ApplyPipelineAsync(request, workspaceRoot ?? string.Empty);
            }
            catch (JsonException jsonEx)
            {
                var errMsg = $"JSON Deserialization Error: {jsonEx.Message}";
                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → {errMsg}");
                return errMsg;
            }
            catch (Exception ex)
            {
                var errMsg = $"Error executing replace_in_file: {ex.Message}";
                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → {errMsg}");
                return errMsg;
            }
        }

        private async Task<string> ApplyPipelineAsync(ReplaceTransactionRequest request, string workspaceRoot)
        {
            var errors = new List<object>();
            var originalContents = new Dictionary<string, string>();
            var newContents = new Dictionary<string, string>();
            var largeFilesTempPaths = new Dictionary<string, string>();
            var largeFilesOriginalContents = new Dictionary<string, string>();

            try
            {
                // 1. Stale-file check with line range validation
                foreach (var fileReq in request.Files)
                {
                    if (File.Exists(fileReq.FilePath))
                    {
                        var lastReadInfo = _fileReadTracker.GetLastReadInfo(fileReq.FilePath);
                        if (lastReadInfo == null)
                        {
                            errors.Add(new { file = WorkspacePathResolver.ToRelativePath(fileReq.FilePath, workspaceRoot), error = "The tool requires reading the file via read_files before editing to ensure stale-file safety. Please read the file first, then retry the edit." });
                            continue;
                        }
                        var lastWrite = File.GetLastWriteTimeUtc(fileReq.FilePath);
                        if (lastWrite > lastReadInfo.Timestamp)
                        {
                            errors.Add(new { file = WorkspacePathResolver.ToRelativePath(fileReq.FilePath, workspaceRoot), error = "File has been modified since you last read it. Please view_file again." });
                            continue;
                        }

                        // Enhanced: Validate that edits target lines that were actually read
                        if (lastReadInfo.StartLine.HasValue && lastReadInfo.EndLine.HasValue)
                        {
                            // The LLM read only a specific line range - validate edit targets are within it
                            var fileContent = await Task.Run(() => File.ReadAllText(fileReq.FilePath));
                            var lines = fileContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                            
                            foreach (var edit in fileReq.Edits)
                            {
                                // Find which lines contain the oldText
                                var oldTextLines = edit.OldText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                                bool foundInReadRange = false;
                                
                                for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
                                {
                                    // Check if oldText starts at this line
                                    if (lines[lineIdx].Contains(oldTextLines[0]))
                                    {
                                        // Verify this line is within the read range (1-indexed)
                                        int targetLine = lineIdx + 1;
                                        if (targetLine >= lastReadInfo.StartLine.Value && targetLine <= lastReadInfo.EndLine.Value)
                                        {
                                            foundInReadRange = true;
                                            break;
                                        }
                                    }
                                }
                                
                                if (!foundInReadRange)
                                {
                                    errors.Add(new 
                                    { 
                                        file = WorkspacePathResolver.ToRelativePath(fileReq.FilePath, workspaceRoot), 
                                        error = $"Edit targets text outside the line range you last read (lines {lastReadInfo.StartLine}-{lastReadInfo.EndLine}). Please read the target region (including the lines containing the text to replace) before editing.",
                                        suggestion = "Use read_files with appropriate line ranges to view the region you want to edit."
                                    });
                                    break; // One error per file is enough
                                }
                            }
                        }
                    }
                    else
                    {
                        errors.Add(new { file = WorkspacePathResolver.ToRelativePath(fileReq.FilePath, workspaceRoot), error = "File not found" });
                    }
                }

                if (errors.Any())
                {
                    return JsonSerializer.Serialize(new { status = "failed", errors = errors }, new JsonSerializerOptions { WriteIndented = true });
                }

                // 2. Validate All Edits via RoslynEditService using batch processing
                // For large files, ProcessLargeFileStreamingAsync will return both the temp path and original content
                foreach (var fileReq in request.Files)
                {
                    var batchEdits = fileReq.Edits.Select(e => new AiAssistant.Engine.Models.BatchEditRequest
                    {
                        OldText = e.OldText,
                        NewText = e.NewText,
                        ExpectedOccurrences = e.ExpectedOccurrences ?? 1
                    }).ToList();

                    var fileInfo = new FileInfo(fileReq.FilePath);
                    if (fileInfo.Length > 1024 * 1024)
                    {
                        var streamResult = await ProcessLargeFileStreamingAsync(fileReq.FilePath, batchEdits);
                        if (!streamResult.Success)
                        {
                            errors.Add(new { file = WorkspacePathResolver.ToRelativePath(fileReq.FilePath, workspaceRoot), message = streamResult.ErrorMessage });
                        }
                        else
                        {
                            largeFilesTempPaths[fileReq.FilePath] = streamResult.TempPath;
                            // Capture the original content returned from streaming (avoids double-read)
                            largeFilesOriginalContents[fileReq.FilePath] = streamResult.OriginalContent;
                        }
                    }
                    else
                    {
                        var content = await Task.Run(() => File.ReadAllText(fileReq.FilePath));
                        originalContents[fileReq.FilePath] = content;
                        var valResult = AiAssistant.Engine.Services.RoslynEditService.TryBuildBatchReplacement(content, batchEdits, null, null);

                        if (!valResult.Success)
                        {
                            errors.Add(new
                            {
                                file = WorkspacePathResolver.ToRelativePath(fileReq.FilePath, workspaceRoot),
                                reason = valResult.Reason,
                                message = valResult.ErrorMessage,
                                tiersAttempted = valResult.TiersAttempted,
                                closestMatch = valResult.ClosestMatch,
                                matchPreviews = valResult.MatchPreviews
                            });
                        }
                        else
                        {
                            newContents[fileReq.FilePath] = valResult.NewContent;
                        }
                    }
                }

                if (errors.Any())
                {
                    return JsonSerializer.Serialize(new { status = "failed", errors = errors }, new JsonSerializerOptions { WriteIndented = true });
                }
            }
            finally
            {
                // Cleanup temp files if validation failed
                if (errors.Any())
                {
                    foreach (var tempPath in largeFilesTempPaths.Values)
                    {
                        try
                        {
                            if (File.Exists(tempPath))
                            {
                                File.Delete(tempPath);
                            }
                        }
                        catch
                        {
                            // Ignore cleanup errors
                        }
                    }
                }
            }

            // 3. Dry Run Check
            if (request.DryRun)
            {
                var diffs = new List<object>();
                foreach (var kvp in newContents)
                {
                    diffs.Add(new { file = WorkspacePathResolver.ToRelativePath(kvp.Key, workspaceRoot), unifiedDiff = ComputeMinimalDiff(originalContents[kvp.Key], kvp.Value) });
                }
                // Clean up large file temp paths in dry run
                foreach (var tempPath in largeFilesTempPaths.Values)
                {
                    try
                    {
                        if (File.Exists(tempPath))
                        {
                            File.Delete(tempPath);
                        }
                    }
                    catch
                    {
                        // Ignore cleanup errors
                    }
                }
                return JsonSerializer.Serialize(new { status = "dry_run", diffs = diffs }, new JsonSerializerOptions { WriteIndented = true });
            }

            // 4. PreviewResolver / Approval Hook (with proper cleanup on rejection)
            try
            {
                if (PreviewResolver != null)
                {
                    bool approved = await PreviewResolver(originalContents, newContents);
                    if (!approved)
                    {
                        // Clean up large file temp paths on rejection
                        foreach (var tempPath in largeFilesTempPaths.Values)
                        {
                            try
                            {
                                if (File.Exists(tempPath))
                                {
                                    File.Delete(tempPath);
                                }
                            }
                            catch
                            {
                                // Ignore cleanup errors
                            }
                        }
                        return "USER REJECTED THIS ACTION. CRITICAL INSTRUCTION: Do NOT retry this tool. Do NOT attempt alternative commands. You MUST STOP and ask the user for further instructions.";
                    }
                }
            }
            catch (Exception previewEx)
            {
                // Clean up large file temp paths on preview error/exception
                foreach (var tempPath in largeFilesTempPaths.Values)
                {
                    try
                    {
                        if (File.Exists(tempPath))
                        {
                            File.Delete(tempPath);
                        }
                    }
                    catch
                    {
                        // Ignore cleanup errors
                    }
                }
                return JsonSerializer.Serialize(new { status = "failed", error = $"Preview resolver failed: {previewEx.Message}" });
            }

            // 5. Diagnostics Hook (Before)
            var preDiagnostics = new List<DiagnosticEntry>();
            try
            {
                preDiagnostics = (await _vsEnvService.GetErrorListDiagnosticsAsync("error")).ToList();
            }
            catch
            {
                // Diagnostics service may not be available; continue without pre-diagnostics
            }

            // 6. Transactional Write with proper snapshot
            // Combine original contents from small files and large files for complete snapshot
            var allOriginalContents = new Dictionary<string, string>(originalContents);
            foreach (var kvp in largeFilesOriginalContents)
            {
                allOriginalContents[kvp.Key] = kvp.Value;
            }

            var transactionId = _transactionService.BeginTransaction(workspaceRoot, allOriginalContents);

            try
            {
                // Write small files
                foreach (var kvp in newContents)
                {
                    if (originalContents[kvp.Key] != kvp.Value)
                    {
                        await AiAssistant.Storage.SafeFileWriter.WriteAllTextAsync(kvp.Key, kvp.Value);
                    }
                }

                // Write large files atomically using File.Replace (atomic on Windows/NTFS)
                foreach (var kvp in largeFilesTempPaths)
                {
                    try
                    {
                        string targetPath = kvp.Key;
                        string tempPath = kvp.Value;
                        string backupPath = targetPath + ".backup_" + Guid.NewGuid().ToString("N");
                        
                        if (File.Exists(targetPath))
                        {
                            // File.Replace is atomic on Windows/NTFS - no window where file doesn't exist
                            // sourceFileName: tempPath (new content)
                            // destinationFileName: targetPath (original file to replace)
                            // destinationBackupFileName: backupPath (backup created atomically)
                            File.Replace(tempPath, targetPath, backupPath);
                            
                            // Clean up backup after successful replacement
                            try
                            {
                                if (File.Exists(backupPath))
                                {
                                    File.Delete(backupPath);
                                }
                            }
                            catch
                            {
                                // Ignore backup cleanup errors - replacement already succeeded
                            }
                        }
                        else
                        {
                            // For new files, just move the temp file
                            File.Move(tempPath, targetPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Roll back all changes on any file write failure
                        await _transactionService.RevertTransactionAsync(workspaceRoot, transactionId);
                        return JsonSerializer.Serialize(new { status = "failed", error = $"Failed to write {WorkspacePathResolver.ToRelativePath(kvp.Key, workspaceRoot)}: {ex.Message}. All changes have been rolled back." });
                    }
                }
            }
            catch (Exception ex)
            {
                await _transactionService.RevertTransactionAsync(workspaceRoot, transactionId);
                return JsonSerializer.Serialize(new { status = "failed", error = $"Write failed, changes rolled back. Exception: {ex.Message}" });
            }

            // 7. Diagnostics Hook (After) - Await WorkspaceChanged or Timeout
            var postDiagnostics = new List<DiagnosticEntry>();
            try
            {
                var tcs = new TaskCompletionSource<bool>();
                EventHandler? workspaceChangedHandler = (s, e) => { tcs.TrySetResult(true); };
                
                _vsEnvService.WorkspaceChanged += workspaceChangedHandler;
                try
                {
                    // Give Roslyn a chance to trigger WorkspaceChanged (max 1200ms for more complex projects)
                    using var cts = new CancellationTokenSource(1200);
                    cts.Token.Register(() => tcs.TrySetResult(false));
                    await tcs.Task.ConfigureAwait(false);
                }
                finally
                {
                    _vsEnvService.WorkspaceChanged -= workspaceChangedHandler;
                }

                postDiagnostics = (await _vsEnvService.GetErrorListDiagnosticsAsync("error")).ToList();
            }
            catch
            {
                // Diagnostics service may not be available; continue without post-diagnostics
            }

            // Compute new diagnostics with structured comparison
            var newDiagnostics = new List<string>();
            if (preDiagnostics.Any() || postDiagnostics.Any())
            {
                // Parse diagnostics into structured format for robust comparison
                var preDiagSet = new HashSet<string>();
                foreach (var diag in preDiagnostics)
                {
                    // Extract error code from description (typically first word after colon, like CS0103, IDE0051, etc.)
                    var spaceIndex = diag.Description.IndexOf(' ');
                    var errorCode = spaceIndex > 0 ? diag.Description.Substring(0, spaceIndex).Trim() : diag.Description.Trim();
                    
                    var key = $"{diag.FileName?.ToLowerInvariant()}|{diag.Line}|{errorCode}";
                    preDiagSet.Add(key);
                }
                
                foreach (var diag in postDiagnostics)
                {
                    var spaceIndex = diag.Description.IndexOf(' ');
                    var errorCode = spaceIndex > 0 ? diag.Description.Substring(0, spaceIndex).Trim() : diag.Description.Trim();
                    
                    var key = $"{diag.FileName?.ToLowerInvariant()}|{diag.Line}|{errorCode}";
                    if (!preDiagSet.Contains(key))
                    {
                        newDiagnostics.Add($"[{diag.Severity}] {diag.FileName}({diag.Line},{diag.Column}): {diag.Description}");
                    }
                }
            }

            // 8. Commit Transaction
            _transactionService.CompleteTransaction(workspaceRoot, transactionId);

            return JsonSerializer.Serialize(new 
            { 
                status = "success", 
                transactionId = transactionId,
                message = $"Successfully replaced content in {request.Files.Count} file(s).",
                diagnostics = newDiagnostics
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        private async Task<(bool Success, string TempPath, string OriginalContent, string ErrorMessage)> ProcessLargeFileStreamingAsync(string filePath, List<AiAssistant.Engine.Models.BatchEditRequest> edits)
        {
            var tempPath = Path.GetTempFileName();
            string originalContent = string.Empty;
            
            try
            {
                // Detect file encoding before processing
                System.Text.Encoding encoding;
                using (var detectionReader = new StreamReader(filePath, true))
                {
                    detectionReader.Peek(); // Trigger encoding detection
                    encoding = detectionReader.CurrentEncoding;
                }

                // Detect line ending style from file
                string lineEnding = "\n";
                using (var detectionReader = new StreamReader(filePath, encoding))
                {
                    var firstLine = await detectionReader.ReadLineAsync();
                    if (firstLine != null)
                    {
                        // Read a small portion to detect line endings
                        var buffer = new char[1024];
                        using (var quickReader = new StreamReader(filePath, encoding))
                        {
                            int charsRead = await quickReader.ReadAsync(buffer, 0, buffer.Length);
                            var fileStart = new string(buffer, 0, charsRead);
                            if (fileStart.Contains("\r\n")) lineEnding = "\r\n";
                            else if (fileStart.Contains("\r")) lineEnding = "\r";
                        }
                    }
                }

                // Phase 1: Validation Pass - Find all matches using line-based coordinate system.
                // Each match records both its start (startLine, startColumn) and the exclusive end
                // (endLine, endColumn) of the matched oldText in the file's line/column space.
                // The end position is required so Phase 3 can slice multi-line oldText correctly
                // (the previous implementation only stored the start, which forced Phase 3 to use
                // sb.Remove(startColumn, span.Length) on a single-line StringBuilder and crash with
                // ArgumentOutOfRangeException whenever oldText spanned more than one line).
                var editMatches = new Dictionary<AiAssistant.Engine.Models.BatchEditRequest, List<(int startLine, int startColumn, int length, string newText, int endLine, int endColumn)>>();
                foreach (var edit in edits)
                {
                    editMatches[edit] = new List<(int startLine, int startColumn, int length, string newText, int endLine, int endColumn)>();
                }

                // Guard: empty edits list. edits.Max(...) below throws InvalidOperationException
                // on an empty sequence, which the catch block converts to a confusing
                // "Large file processing failed: Sequence contains no elements" message.
                // Reject explicitly with a clear error instead. (edits is never null — the
                // caller builds it via .Select(...).ToList() — but it can be empty when the
                // request JSON sends "edits": [].)
                if (edits == null || edits.Count == 0)
                {
                    return (false, tempPath, originalContent, "No edits provided for large file processing.");
                }

                int maxOldTextLines = edits.Max(e => e.OldText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None).Length);
                int windowSize = Math.Max(2000, maxOldTextLines * 2);
                int overlapLines = Math.Max(100, maxOldTextLines + 10);

                // Defensive guard: if the formulas above ever change such that overlapLines
                // >= windowSize, the window advance (windowSize - overlapLines) would be <= 0
                // and the while loop would never terminate. The current formulas guarantee
                // windowSize > overlapLines for all maxOldTextLines >= 1, but lock that in
                // explicitly so a future tweak can't silently introduce an infinite loop.
                if (windowSize - overlapLines <= 0)
                {
                    return (false, tempPath, originalContent,
                        $"Internal error: window configuration invalid (windowSize={windowSize}, overlapLines={overlapLines}).");
                }

                // Pre-compute oldText's per-line segments once per edit. Phase 1 previously
                // called edit.OldText.Split(...) inside the innermost match loop (once per
                // match found), which re-split the same string O(matches) times. For edits
                // that match many times (e.g., a common identifier rename), this was pure
                // wasted allocation. Caching the split result per edit eliminates the redo.
                var oldTextPartsByEdit = new Dictionary<AiAssistant.Engine.Models.BatchEditRequest, string[]>(edits.Count);
                foreach (var edit in edits)
                {
                    oldTextPartsByEdit[edit] = edit.OldText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
                }

                var allLines = new List<string>();
                using (var reader = new StreamReader(filePath, encoding))
                {
                    string line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        allLines.Add(line);
                    }
                }
                
                // Capture original content for snapshot (single read, no duplication)
                originalContent = string.Join(lineEnding, allLines);
                if (!string.IsNullOrEmpty(originalContent) && !originalContent.EndsWith(lineEnding))
                {
                    // Preserve whether file ended with newline or not
                    var lastChar = await Task.Run(() =>
                    {
                        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        if (fs.Length > 0)
                        {
                            fs.Seek(-1, SeekOrigin.End);
                            return (char)fs.ReadByte();
                        }
                        return '\0';
                    });
                    if (lastChar == '\n' || lastChar == '\r')
                    {
                        originalContent += lineEnding;
                    }
                }

                // Track whether the original file had a trailing newline so Phase 3 can
                // preserve that property. The previous Phase 3 unconditionally called
                // WriteLineAsync for every line, which silently appended a trailing
                // newline to files that did not originally have one (a pre-existing bug
                // that affects even trivial single-line -> single-line edits).
                bool fileHasNoTrailingNewline = !string.IsNullOrEmpty(originalContent)
                    && !originalContent.EndsWith(lineEnding);

                // Process in sliding windows to find all matches
                int windowStart = 0;
                var seenMatches = new HashSet<string>(); // Deduplicate by "edit_line_column"

                while (windowStart < allLines.Count)
                {
                    int windowEnd = Math.Min(windowStart + windowSize, allLines.Count);
                    int windowCount = windowEnd - windowStart;
                    // Use GetRange (O(windowCount) direct array copy) instead of
                    // Skip(windowStart).Take(windowCount).ToList(). The LINQ version iterates
                    // through the first windowStart elements on every window, making the total
                    // match-finding phase O(N^2 / step) in line skips alone. For a 100k-line
                    // file with step=1900, that's ~2.6B skipped iterations. GetRange copies
                    // the range directly with no traversal overhead.
                    var windowLines = allLines.GetRange(windowStart, windowCount);
                    string windowContent = string.Join(lineEnding, windowLines);

                    // Try each edit against this window
                    foreach (var edit in edits)
                    {
                        var singleEdit = new List<AiAssistant.Engine.Models.BatchEditRequest>
                        {
                            new AiAssistant.Engine.Models.BatchEditRequest
                            {
                                OldText = edit.OldText,
                                NewText = edit.NewText,
                                ExpectedOccurrences = 1
                            }
                        };

                        var validationResult = AiAssistant.Engine.Services.RoslynEditService.TryBuildBatchReplacement(
                            windowContent, singleEdit, null, null);

                        if (validationResult.Success && validationResult.Spans != null)
                        {
                            foreach (var span in validationResult.Spans)
                            {
                                // Convert character offset to line/column within window (start position)
                                int charCount = 0;
                                int startLine = windowStart;
                                int startColumn = 0;

                                for (int i = 0; i < windowLines.Count; i++)
                                {
                                    int lineLength = windowLines[i].Length + lineEnding.Length;
                                    if (charCount + lineLength > span.StartIndex)
                                    {
                                        startLine = windowStart + i;
                                        startColumn = span.StartIndex - charCount;
                                        break;
                                    }
                                    charCount += lineLength;
                                }

                                // Compute end line/column from oldText's structure.
                                //
                                // The match was found by searching for edit.OldText inside windowContent
                                // (which joins windowLines with lineEnding), so oldText's line breaks must
                                // already match lineEnding - otherwise TryBuildBatchReplacement would
                                // not have returned a span. Splitting oldText therefore yields the
                                // per-line segments of the match, and the last segment's length is the
                                // column offset on endLine where the match ends (exclusive).
                                //
                                // This is more robust than a second CharOffsetToLineColumn call on
                                // (startIndex + length): that approach is ambiguous when the end offset
                                // falls exactly on a line break boundary (it would report column 0 of
                                // the *next* line, which misrepresents what the match consumed). Walking
                                // oldText's own segments sidesteps the boundary case entirely.
                                int endLine, endColumn;
                                var oldTextParts = oldTextPartsByEdit[edit];
                                if (oldTextParts.Length == 1)
                                {
                                    // Single-line oldText: end column is start + oldText length.
                                    // (span.Length == edit.OldText.Length here, so this is consistent.)
                                    endLine = startLine;
                                    endColumn = startColumn + edit.OldText.Length;
                                }
                                else
                                {
                                    // Multi-line oldText: endLine is startLine + (number of line breaks).
                                    // endColumn is the length of oldText's last segment, i.e. the column
                                    // on endLine where the match ends. If oldText ends with a line break,
                                    // the last segment is the empty string and endColumn == 0 - meaning
                                    // the match consumed the previous line plus its trailing break and
                                    // terminates at column 0 of the next line.
                                    endLine = startLine + oldTextParts.Length - 1;
                                    endColumn = oldTextParts[oldTextParts.Length - 1].Length;
                                }

                                // Deduplicate: same edit at same position.
                                // Key format (OldText_StartLine_StartColumn) is intentionally preserved
                                // from the original implementation - the plan's ask was to carry it
                                // forward onto the new tuple fields, not to change it.
                                string matchKey = $"{edit.OldText}_{startLine}_{startColumn}";
                                if (!seenMatches.Contains(matchKey))
                                {
                                    seenMatches.Add(matchKey);
                                    editMatches[edit].Add((startLine, startColumn, span.Length, span.NewText, endLine, endColumn));

                                    // Early termination if we've found too many matches
                                    if (editMatches[edit].Count > edit.ExpectedOccurrences)
                                    {
                                        return (false, tempPath, originalContent,
                                            $"Expected {edit.ExpectedOccurrences} occurrence(s) but found more than that. Match limit exceeded at line {startLine + 1}.");
                                    }
                                }
                            }
                        }
                    }

                    // Move window forward, with overlap to catch matches across boundaries
                    windowStart += (windowSize - overlapLines);
                }

                // Validate all edits found expected number of occurrences
                foreach (var edit in edits)
                {
                    if (editMatches[edit].Count != edit.ExpectedOccurrences)
                    {
                        return (false, tempPath, originalContent,
                            $"Expected {edit.ExpectedOccurrences} occurrence(s) for edit, found {editMatches[edit].Count}.");
                    }
                }

                // Phase 2: Build replacement map by line. The tuple now carries endLine/endColumn
                // (derived from oldText's real span in Phase 1) so the cross-line overlap check
                // can key off the lines actually consumed by oldText rather than newText's line count.
                var replacementsByLine = new Dictionary<int, List<(int startColumn, int length, string newText, int endLine, int endColumn)>>();
                foreach (var edit in edits)
                {
                    foreach (var match in editMatches[edit])
                    {
                        // TryAdd pattern: single hash lookup instead of ContainsKey + indexer (two lookups).
                        if (!replacementsByLine.TryGetValue(match.startLine, out var list))
                        {
                            list = new List<(int startColumn, int length, string newText, int endLine, int endColumn)>();
                            replacementsByLine[match.startLine] = list;
                        }
                        list.Add((match.startColumn, match.length, match.newText, match.endLine, match.endColumn));
                    }
                }

                // Sort replacements within each line by start column (reverse order for safe application)
                foreach (var lineReplacements in replacementsByLine.Values)
                {
                    lineReplacements.Sort((a, b) => b.startColumn.CompareTo(a.startColumn));
                }

                // Check for overlapping edits within same line and detect multi-line replacements with multiple edits.
                // Also check for cross-line overlaps where a multi-line oldText replacement would conflict
                // with edits on lines the match actually consumes.
                foreach (var kvp in replacementsByLine)
                {
                    var lineRepls = kvp.Value;

                    // Two distinct multi-line conditions must force "only edit on this line":
                    //   * hasMultiLineOldText: oldText spans more than one original line. Phase 3
                    //     slices the startLine at startColumn (prefix) and endLine at endColumn
                    //     (suffix), so any neighbor edit on startLine would corrupt the slice.
                    //   * hasMultiLineNewText: oldText is single-line but newText expands into
                    //     multiple lines. Phase 3 splits the line at start/endColumn and writes
                    //     newText's lines between, so neighbor edits would again be lost.
                    // The previous implementation only checked newText for '\n', which meant a
                    // multi-line oldText -> single-line newText replacement (the crash case)
                    // bypassed this guard entirely.
                    bool hasMultiLineOldText = lineRepls.Any(r => r.endLine > kvp.Key);
                    bool hasMultiLineNewText = lineRepls.Any(r => r.newText.Contains('\n'));

                    if ((hasMultiLineOldText || hasMultiLineNewText) && lineRepls.Count > 1)
                    {
                        return (false, tempPath, originalContent,
                            $"Cannot apply multiple edits on line {kvp.Key + 1} when one or more replacements span multiple lines. " +
                            $"Please use separate edit entries with more specific oldText to target each location uniquely.");
                    }

                    // Check within-line overlaps. This only runs when every replacement on this
                    // line is single-line oldText (the multi-line cases were rejected above), so
                    // r.length == oldText.Length is the correct in-line character count here.
                    for (int i = 0; i < lineRepls.Count - 1; i++)
                    {
                        var curr = lineRepls[i];
                        var next = lineRepls[i + 1];
                        if (curr.startColumn < next.startColumn + next.length)
                        {
                            return (false, tempPath, originalContent,
                                $"Overlapping edits detected on line {kvp.Key + 1}. Cannot apply safely.");
                        }
                    }

                    // Cross-line overlap check: key off oldText's real EndLine, NOT newText's line count.
                    //
                    // The previous implementation used newText.Split(...).Length to decide how many
                    // subsequent lines to protect, which was wrong in two stacking ways:
                    //   (1) It only triggered when newText contained '\n', so a multi-line oldText ->
                    //       single-line newText replacement (the crash case) never entered this check
                    //       at all - downstream Phase 3 would still throw.
                    //   (2) Even when it did trigger, it protected the wrong span: the lines that need
                    //       protection are the ones oldText consumes (startLine+1 .. endLine), not the
                    //       lines newText would produce.
                    // Both are fixed by reading endLine off the tuple (computed from oldText in Phase 1)
                    // and using an inclusive bound (endLine is always consumed in Phase 3 because its
                    // content - or the absence of it, when endColumn == 0 - becomes the suffix of the
                    // replacement's last output line).
                    if (hasMultiLineOldText)
                    {
                        var multiLineOldRepl = lineRepls.First(r => r.endLine > kvp.Key);

                        for (int checkLine = kvp.Key + 1; checkLine <= multiLineOldRepl.endLine && checkLine < allLines.Count; checkLine++)
                        {
                            if (replacementsByLine.ContainsKey(checkLine))
                            {
                                return (false, tempPath, originalContent,
                                    $"Cross-line overlap detected: Multi-line oldText replacement starting at line {kvp.Key + 1} " +
                                    $"consumes through line {multiLineOldRepl.endLine + 1}, which has its own edit. " +
                                    $"This could cause unpredictable results. Please use more specific oldText to avoid ambiguity.");
                            }
                        }
                    }
                }

                // Phase 3: Apply replacements and write output atomically.
                //
                // Two pre-existing bugs are fixed here:
                //
                //   1. Multi-line oldText crash. The previous implementation applied every
                //      replacement via sb.Remove(repl.column, repl.length) where sb was a
                //      StringBuilder over a single line and repl.length was span.Length - a
                //      character count that can span multiple lines. For any multi-line oldText,
                //      repl.length exceeds the remaining characters in the line and sb.Remove
                //      throws ArgumentOutOfRangeException, failing the whole operation.
                //
                //   2. Trailing newline corruption. The previous implementation unconditionally
                //      called WriteLineAsync for every line, including the last, so any edit -
                //      even a trivial single-line -> single-line replacement - appended a trailing
                //      newline to files that did not originally have one.
                //
                // The new structure builds outputLines first (so multi-line replacements can fold
                // multiple original lines into one output line or expand one original line into
                // many), then writes them with explicit trailing-newline control.
                var outputLines = new List<string>(allLines.Count);
                var consumedLines = new HashSet<int>();
                int lineIdx = 0;

                while (lineIdx < allLines.Count)
                {
                    if (consumedLines.Contains(lineIdx))
                    {
                        // This line was already written as part of a multi-line oldText
                        // replacement (its content is folded into the suffix of a previous
                        // output line). Skip it.
                        lineIdx++;
                        continue;
                    }

                    if (replacementsByLine.TryGetValue(lineIdx, out var lineRepls))
                    {
                        // Case A: multi-line oldText replacement. Phase 2 validation guarantees
                        // this is the only edit on its startLine, so the First() below is safe.
                        var multiLineOldRepl = lineRepls.FirstOrDefault(r => r.endLine > lineIdx);
                        if (multiLineOldRepl.endLine > lineIdx)
                        {
                            string originalLine = allLines[lineIdx];
                            string prefix = originalLine.Substring(0, multiLineOldRepl.startColumn);

                            // Suffix comes from endLine at endColumn. If endLine == allLines.Count,
                            // the match consumed the file's trailing line break and there is no
                            // suffix line in allLines - the replacement effectively ends the file.
                            string endSuffix = multiLineOldRepl.endLine < allLines.Count
                                ? allLines[multiLineOldRepl.endLine].Substring(multiLineOldRepl.endColumn)
                                : string.Empty;

                            var newTextLines = multiLineOldRepl.newText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

                            if (newTextLines.Length == 1)
                            {
                                // Multi-line oldText collapses into a single output line:
                                // prefix + newText + suffix. This handles the previously-crashing
                                // multi-line oldText -> single-line newText case.
                                outputLines.Add(prefix + newTextLines[0] + endSuffix);
                            }
                            else
                            {
                                // Expand: first output line is prefix + newTextLines[0],
                                // middle output lines are newTextLines[1..^1],
                                // last output line is newTextLines[^1] + endSuffix.
                                outputLines.Add(prefix + newTextLines[0]);
                                for (int i = 1; i < newTextLines.Length - 1; i++)
                                {
                                    outputLines.Add(newTextLines[i]);
                                }
                                outputLines.Add(newTextLines[newTextLines.Length - 1] + endSuffix);
                            }

                            // Mark consumed lines (startLine+1 through endLine inclusive). endLine
                            // is always consumed because its content (or the absence of it, when
                            // endLine == allLines.Count) is part of the replacement's last output
                            // line. A replacement starting on any of these lines was already
                            // rejected by the Phase 2 cross-line overlap check.
                            for (int cl = lineIdx + 1; cl <= multiLineOldRepl.endLine && cl < allLines.Count; cl++)
                            {
                                consumedLines.Add(cl);
                            }

                            lineIdx = multiLineOldRepl.endLine + 1;
                            continue;
                        }

                        // Case B: single-line oldText + multi-line newText. Phase 2 validation
                        // guarantees this is the only edit on its startLine.
                        var multiLineNewRepl = lineRepls.FirstOrDefault(r => r.newText.Contains('\n'));
                        if (multiLineNewRepl.newText != null && multiLineNewRepl.newText.Contains('\n'))
                        {
                            string originalLine = allLines[lineIdx];
                            string prefix = originalLine.Substring(0, multiLineNewRepl.startColumn);
                            // For single-line oldText, endColumn == startColumn + oldText.Length,
                            // so Substring(endColumn) yields the unchanged suffix of this line.
                            string suffix = originalLine.Substring(multiLineNewRepl.endColumn);

                            var newTextLines = multiLineNewRepl.newText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

                            outputLines.Add(prefix + newTextLines[0]);
                            for (int i = 1; i < newTextLines.Length - 1; i++)
                            {
                                outputLines.Add(newTextLines[i]);
                            }
                            outputLines.Add(newTextLines[newTextLines.Length - 1] + suffix);

                            lineIdx++;
                            continue;
                        }

                        // Case C: all single-line replacements. Apply in reverse column order
                        // (already sorted in Phase 2) so earlier removes don't shift later
                        // positions. sb.Remove(startColumn, length) is safe here because every
                        // replacement on this line has length == oldText.Length (single-line).
                        var sb = new System.Text.StringBuilder(allLines[lineIdx]);
                        foreach (var repl in lineRepls)
                        {
                            sb.Remove(repl.startColumn, repl.length);
                            sb.Insert(repl.startColumn, repl.newText);
                        }
                        outputLines.Add(sb.ToString());
                    }
                    else
                    {
                        // No replacements on this line.
                        outputLines.Add(allLines[lineIdx]);
                    }

                    lineIdx++;
                }

                // Write output lines, preserving the original file's trailing-newline property.
                // The previous implementation always used WriteLineAsync for the last line, which
                // added a trailing newline even when the source file had none. Now we use
                // WriteAsync (no newline) for the final line iff the source lacked one.
                using (var writer = new StreamWriter(tempPath, false, encoding))
                {
                    writer.NewLine = lineEnding;

                    for (int i = 0; i < outputLines.Count; i++)
                    {
                        if (i == outputLines.Count - 1 && fileHasNoTrailingNewline)
                        {
                            await writer.WriteAsync(outputLines[i]);
                        }
                        else
                        {
                            await writer.WriteLineAsync(outputLines[i]);
                        }
                    }
                }

                return (true, tempPath, originalContent, string.Empty);
            }
            catch (Exception ex)
            {
                // Clean up temp file on any error
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // Ignore cleanup errors
                }

                return (false, tempPath, originalContent, $"Large file processing failed: {ex.Message}");
            }
        }

        private static string ComputeMinimalDiff(string oldContent, string newContent)
        {
            var oldLines = oldContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var newLines = newContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            
            // Simple chunked diff to avoid dumping hundreds of unchanged lines.
            // Using a basic heuristic to find changed segments.
            var diff = new System.Text.StringBuilder();
            int i = 0, j = 0;
            int context = 3;

            while (i < oldLines.Length || j < newLines.Length)
            {
                // Find matching sequences
                int matchI = i, matchJ = j;
                int maxMatch = 0;
                
                for (int tryI = i; tryI < oldLines.Length && tryI < i + 100; tryI++)
                {
                    for (int tryJ = j; tryJ < newLines.Length && tryJ < j + 100; tryJ++)
                    {
                        if (oldLines[tryI] == newLines[tryJ])
                        {
                            int matchLen = 0;
                            while (tryI + matchLen < oldLines.Length && tryJ + matchLen < newLines.Length && 
                                   oldLines[tryI + matchLen] == newLines[tryJ + matchLen])
                                matchLen++;

                            if (matchLen > maxMatch)
                            {
                                maxMatch = matchLen;
                                matchI = tryI;
                                matchJ = tryJ;
                            }
                        }
                    }
                }

                if (maxMatch == 0) // No matches found ahead, rest is replaced
                {
                    matchI = oldLines.Length;
                    matchJ = newLines.Length;
                }

                // We have a mismatch from (i, j) to (matchI, matchJ)
                if (matchI > i || matchJ > j)
                {
                    int printStartOld = Math.Max(0, i - context);
                    int printEndOld = matchI - 1;
                    int printStartNew = Math.Max(0, j - context);
                    int printEndNew = matchJ - 1;
                    
                    diff.AppendLine($"@@ -{printStartOld + 1},{printEndOld - printStartOld + 1} +{printStartNew + 1},{printEndNew - printStartNew + 1} @@");
                    
                    for (int c = printStartOld; c < i; c++) diff.AppendLine($" {oldLines[c]}");
                    for (int c = i; c < matchI; c++) diff.AppendLine($"-{oldLines[c]}");
                    for (int c = j; c < matchJ; c++) diff.AppendLine($"+{newLines[c]}");
                    for (int c = matchI; c < Math.Min(oldLines.Length, matchI + context); c++) diff.AppendLine($" {oldLines[c]}");
                }

                if (maxMatch == 0) break;

                i = matchI + maxMatch;
                j = matchJ + maxMatch;
            }

            return diff.ToString();
        }
    }
}


