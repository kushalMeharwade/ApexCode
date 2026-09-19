using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AiAssistant.UI.ViewModels;

/// <summary>
/// A single observable entry in the assistant's chronological activity trace.
/// It intentionally keeps the tool payload available, while presenting a concise,
/// human-readable summary until the entry is expanded.
/// </summary>
public class ToolCallViewModel : IMessageElement, INotifyPropertyChanged
{
    private string _id = string.Empty;
    private string _toolName = string.Empty;
    private string _parameters = string.Empty;
    private string _statusText = "Running…";
    private string _resultText = string.Empty;
    private bool _needsApproval;
    private bool _hasResult;
    private bool _isExpanded;
    private DateTime _occurredAt = DateTime.Now;
    private readonly List<ToolAttempt> _previousAttempts = new();
    private readonly Dictionary<string, string> _arguments = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasParsedArguments;
    private int _presentationUpdateDepth;
    private bool _presentationRefreshPending;
    private string _rawRemovedText = string.Empty;
    private string _rawAddedText = string.Empty;

    private sealed class ToolAttempt
    {
        public ToolAttempt(DateTime occurredAt, string statusText, string parameters, string resultText)
        {
            OccurredAt = occurredAt;
            StatusText = statusText;
            Parameters = parameters;
            ResultText = resultText;
        }

        public DateTime OccurredAt { get; }
        public string StatusText { get; }
        public string Parameters { get; }
        public string ResultText { get; }
    }

    public string Id
    {
        get => _id;
        set { _id = value; OnPropertyChanged(); }
    }

    public string ToolName
    {
        get => _toolName;
        set
        {
            if (string.Equals(_toolName, value, StringComparison.Ordinal)) return;
            _toolName = value;
            OnPropertyChanged();
            RefreshPresentation();
        }
    }

    public string Parameters
    {
        get => _parameters;
        set
        {
            if (string.Equals(_parameters, value, StringComparison.Ordinal)) return;
            _parameters = value;
            RebuildArgumentCache();
            OnPropertyChanged();
            RefreshPresentation();
        }
    }

    public bool HasParameters => !string.IsNullOrWhiteSpace(Parameters);

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (string.Equals(_statusText, value, StringComparison.Ordinal)) return;
            _statusText = value;
            OnPropertyChanged();
            RefreshPresentation();
        }
    }

    public string ResultText
    {
        get => _resultText;
        set
        {
            if (string.Equals(_resultText, value, StringComparison.Ordinal)) return;
            _resultText = value;
            OnPropertyChanged();
            HasResult = !string.IsNullOrWhiteSpace(value);
            RefreshPresentation();
        }
    }

    public bool NeedsApproval
    {
        get => _needsApproval;
        set
        {
            if (_needsApproval == value) return;
            _needsApproval = value;
            OnPropertyChanged();
            RefreshPresentation();
        }
    }

    public bool HasResult
    {
        get => _hasResult;
        set
        {
            if (_hasResult == value) return;
            _hasResult = value;
            OnPropertyChanged();
            RefreshPresentation();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public DateTime OccurredAt
    {
        get => _occurredAt;
        set { _occurredAt = value; OnPropertyChanged(); }
    }

    public int AttemptCount => _previousAttempts.Count + 1;
    public bool HasAttemptHistory => _previousAttempts.Count > 0;

    public string AttemptHistory => string.Join(
        Environment.NewLine + Environment.NewLine,
        _previousAttempts.Select((attempt, index) =>
        {
            var details = string.IsNullOrWhiteSpace(attempt.ResultText)
                ? attempt.Parameters
                : attempt.ResultText;
            return $"Attempt {index + 1} · {attempt.OccurredAt:HH:mm:ss} · {attempt.StatusText}" +
                   (string.IsNullOrWhiteSpace(details) ? string.Empty : Environment.NewLine + details);
        }));

    public string OperationIconKey => ToolName?.Trim().ToLowerInvariant() switch
    {
        "read_files" or "read_file" => "IconFile",
        "replace_in_file" or "create_file" or "replace_file_content" or "multi_replace_file_content" or "write_to_file" => "IconEdit",
        "search_codebase" => "IconSearch",
        "list_files" or "list_workspace_files" => "IconFolder",
        "execute_command" or "read_command_output" or "stop_command" => "IconTerminal",
        "__result__" => "IconCopy",
        _ => "IconSettings"
    };

    public string OperationLabel => ToolName?.Trim().ToLowerInvariant() switch
    {
        "read_files" => "Reading",
        "read_file" => "Reading",
        "replace_in_file" => "Modified",
        "replace_file_content" => "Modified",
        "multi_replace_file_content" => "Modified",
        "create_file" => "Created",
        "write_to_file" => "Created",
        "search_codebase" => "Searching",
        "list_files" => "Listing",
        "list_workspace_files" => "Listing",
        "get_workspace_context" => "Inspected",
        "get_diagnostics" => "Diagnostics",
        "get_symbol_references" => "Found References",
        "get_type_hierarchy" => "Inspected Hierarchy",
        "execute_command" => "Executing",
        "__result__" => "Result",
        _ => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase((ToolName ?? "tool").Replace('_', ' '))
    };

    public string? AbsolutePath
    {
        get
        {
            var path = GetArgument("filePath") ?? GetArgument("path") ?? GetArgument("TargetFile") ?? GetArgument("AbsolutePath") ?? GetArgument("Url");
            if (!string.IsNullOrWhiteSpace(path)) return path;

            var filePaths = GetArgument("files") ?? GetArgument("filePaths");
            if (!string.IsNullOrWhiteSpace(filePaths))
            {
                var extractedPath = TryExtractFirstFilePathRobust(filePaths, out _);
                if (!string.IsNullOrWhiteSpace(extractedPath)) return extractedPath;
            }
            return null;
        }
    }

    public bool IsAttemptCompletion => string.Equals(ToolName, "attempt_completion", StringComparison.OrdinalIgnoreCase);

    public string Target
    {
        get
        {
            string? targetPath = null;
            var filePath = GetArgument("filePath") ?? GetArgument("path") ?? GetArgument("TargetFile") ?? GetArgument("AbsolutePath") ?? GetArgument("Url");
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                targetPath = ShortenPath(filePath!);
            }
            else
            {
                var filePaths = GetArgument("files") ?? GetArgument("filePaths");
                if (!string.IsNullOrWhiteSpace(filePaths))
                {
                    targetPath = ExtractFilePath(filePaths) ?? ShortenPath(filePaths);
                }
            }

            if (targetPath != null)
            {
                if (IsReadFiles)
                {
                    var startLineStr = GetArgument("startLine");
                    var endLineStr = GetArgument("endLine");

                    if (string.IsNullOrWhiteSpace(startLineStr) || string.IsNullOrWhiteSpace(endLineStr))
                    {
                        var filesArg = GetArgument("files");
                        if (!string.IsNullOrWhiteSpace(filesArg))
                        {
                            using JsonDocument? doc = TryParse(filesArg);
                            if (doc != null && doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                            {
                                var firstElement = doc.RootElement[0];
                                if (firstElement.ValueKind == JsonValueKind.Object)
                                {
                                    if (firstElement.TryGetProperty("startLine", out var sl) || firstElement.TryGetProperty("start", out sl) || firstElement.TryGetProperty("StartLine", out sl)) startLineStr ??= sl.ToString();
                                    if (firstElement.TryGetProperty("endLine", out var el) || firstElement.TryGetProperty("end", out el) || firstElement.TryGetProperty("EndLine", out el)) endLineStr ??= el.ToString();
                                }
                            }
                            else
                            {
                                var startMatch = System.Text.RegularExpressions.Regex.Match(filesArg, @"\""(?:startLine|start|StartLine)\""\s*:\s*(\d+)");
                                if (startMatch.Success) startLineStr ??= startMatch.Groups[1].Value;

                                var endMatch = System.Text.RegularExpressions.Regex.Match(filesArg, @"\""(?:endLine|end|EndLine)\""\s*:\s*(\d+)");
                                if (endMatch.Success) endLineStr ??= endMatch.Groups[1].Value;
                            }
                        }
                    }

                    int startLine = 1;
                    if (int.TryParse(startLineStr, out int s)) startLine = s;

                    int endLine = 0;
                    if (int.TryParse(endLineStr, out int e)) endLine = e;

                    if (startLine > 1 || endLine > 0)
                    {
                        if (endLine > 0)
                            return $"{targetPath} L{startLine}-{endLine}";
                        else
                            return $"{targetPath} L{startLine}+";
                    }
                }
                return targetPath;
            }

            return GetArgument("pattern")
                ?? GetArgument("command")
                ?? GetArgument("CommandLine")
                ?? GetArgument("symbolName")
                ?? GetArgument("typeName")
                ?? GetArgument("query")
                ?? GetArgument("directory")
                ?? (ToolName == "__result__" ? ResultSummary : string.Empty);
        }
    }

    public string ResultSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ResultText)) return string.Empty;
            var firstLine = ResultText.Replace("\r", string.Empty)
                .Split('\n')
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? string.Empty;
            return Truncate(firstLine, 96);
        }
    }

    public bool HasResultSummary => !string.IsNullOrWhiteSpace(ResultSummary);

    public string ParameterDetails
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Parameters)) return string.Empty;
            if (!_hasParsedArguments) return Parameters;

            var lines = _arguments
                .Where(property => !IsFileMutation ||
                    (property.Key != "oldText" && property.Key != "newText" && property.Key != "content"))
                .Select(property => $"{property.Key}: {property.Value}");
            return string.Join(Environment.NewLine, lines);
        }
    }

    public bool HasDisplayParameters => !string.IsNullOrWhiteSpace(ParameterDetails);

    public string HeaderSummary
    {
        get
        {
            if (CommandStatus != null) return "Command " + CommandStatus.Replace('_', ' ');
            if (NeedsApproval || (!HasResult && !string.Equals(StatusText, "Completed", StringComparison.OrdinalIgnoreCase)))
                return StatusText;
            if (AttemptCount > 1)
                return $"{AttemptCount} attempts";
            if (HasChangePreview)
                return string.Empty; // Replaced by distinct UI elements in XAML
            return ToolName == "__result__" ? ResultSummary : string.Empty;
        }
    }

    public bool HasDetails => HasParameters || HasResult || HasChangePreview || HasAttemptHistory;

    /// <summary>True while the call is actively executing (no result yet, and not waiting on approval).
    /// Drives the pulsing "Running…" indicator; flips immediately to the static completed state once
    /// <see cref="HasResult"/> or <see cref="NeedsApproval"/> changes, with no animation delay.</summary>
    public bool IsRunning => (ToolName == "execute_command" && CommandStatus == "running" || !IsTerminal) && !NeedsApproval;
    
    public bool IsTerminal => ToolName == "execute_command" && CommandStatus == "running" ? false : HasResult ||
                              StatusText.IndexOf("complete", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              StatusText.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              StatusText.IndexOf("reject", StringComparison.OrdinalIgnoreCase) >= 0;
    public bool HasChangePreview => IsFileMutation && (!string.IsNullOrWhiteSpace(RemovedText) || !string.IsNullOrWhiteSpace(AddedText));
    public bool IsFileMutation => ToolName is "replace_in_file" or "create_file" or "replace_file_content" or "multi_replace_file_content" or "write_to_file";
    public bool IsFileOperation => IsFileMutation || ToolName?.Trim().ToLowerInvariant() is "read_files" or "read_file" or "view_file" or "search_codebase" or "list_files" or "list_workspace_files" or "get_workspace_context";
    public bool IsSchemaOrDataOperation => ToolName?.IndexOf("schema", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           ToolName?.IndexOf("database", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           ToolName?.IndexOf("query", StringComparison.OrdinalIgnoreCase) >= 0;
    public bool IsShellExecution => ToolName is "execute_command" or "read_command_output" or "stop_command";
    public bool IsReadFiles => ToolName?.Trim().ToLowerInvariant() is "read_files" or "read_file" or "view_file" or "read_url_content";
    public bool IsSearchCodebase => ToolName?.Trim().ToLowerInvariant() is "search_codebase" or "search_code_base" or "grep_search";
    public bool IsListFiles => ToolName?.Trim().ToLowerInvariant() is "list_files" or "list_workspace_files" or "list_dir";
    public bool HasCustomView => IsShellExecution || IsSearchCodebase || IsListFiles || IsReadFiles;

    public bool IsErrorExitCode
    {
        get
        {
            if (!IsShellExecution) return false;
            if (CommandStatus is "failed" or "cancelled" or "timed_out") return true;
            var match = System.Text.RegularExpressions.Regex.Match(ResultText ?? "", @"\[ExitCode:\s*(-?\d+)\]");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int code))
                return code != 0;
            return false;
        }
    }

    private string? CommandStatus
    {
        get
        {
            if (!IsShellExecution || string.IsNullOrEmpty(ResultText)) return null;
            try
            {
                using var json = JsonDocument.Parse(ResultText);
                return json.RootElement.TryGetProperty("session_id", out _) && json.RootElement.TryGetProperty("status", out var status) ? status.GetString() : null;
            }
            catch (JsonException) { return null; }
        }
    }

    public string TerminalOutput
    {
        get
        {
            if (!IsShellExecution) return string.Empty;
            if (CommandStatus != null)
            {
                try
                {
                    using var json = JsonDocument.Parse(ResultText);
                    var root = json.RootElement;
                    var output = root.GetProperty("output").GetString() ?? "";
                    return "[" + CommandStatus + "] " + root.GetProperty("session_id").GetString() + Environment.NewLine +
                        output + Environment.NewLine + "Log: " + root.GetProperty("log_path").GetString();
                }
                catch (JsonException) { }
            }
            return System.Text.RegularExpressions.Regex.Replace(ResultText ?? "", @"^\[ExitCode:\s*-?\d+\]\s*\n?", "");
        }
    }

    public string CustomExpandedViewText
    {
        get
        {
            if (IsReadFiles)
            {
                var files = new List<string>();
                var matches = System.Text.RegularExpressions.Regex.Matches(ResultText ?? "", @"File:\s*(.+?)\nTotal Lines:\s*(\d+)(?:\nShowing Lines:\s*([^\n]+))?");
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    var path = ShortenPath(match.Groups[1].Value);
                    var totalLines = match.Groups[2].Value;
                    var showingLines = match.Groups[3].Success ? match.Groups[3].Value.Trim() : null;
                    
                    if (!string.IsNullOrEmpty(showingLines) && showingLines != $"1-{totalLines}")
                    {
                        files.Add($"• {path} (lines {showingLines} of {totalLines})");
                    }
                    else
                    {
                        files.Add($"• {path} ({totalLines} lines)");
                    }
                }
                return files.Count > 0 ? string.Join("\n", files) : (ResultText ?? "");
            }
            if (IsSearchCodebase)
            {
                var parts = (ResultText ?? "").Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    return string.Join("\n\n", parts.Take(3)) + (parts.Length > 3 ? $"\n\n... and {parts.Length - 3} more matches" : "");
                }
                return ResultText ?? "";
            }
            if (IsListFiles)
            {
                return ResultText ?? "";
            }
            return string.Empty;
        }
    }

    public bool IsFailure => CommandStatus != null ? IsErrorExitCode : NeedsApproval || IsErrorExitCode ||
                             StatusText.IndexOf("reject", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             StatusText.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             StatusText.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             ResultText.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0;
    
    public string RemovedText => Truncate(_rawRemovedText, 1400);

    public string AddedText => Truncate(_rawAddedText, 1400);

    internal string RawRemovedText => _rawRemovedText;
    internal string RawAddedText => _rawAddedText;

    public int RemovedLineCount => ComputeDiff().Removed;
    public int AddedLineCount => ComputeDiff().Added;

    private (int Added, int Removed) ComputeDiff()
    {
        var oldLines = _rawRemovedText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var newLines = _rawAddedText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        
        bool oldEmpty = oldLines.Length == 0 || (oldLines.Length == 1 && string.IsNullOrEmpty(oldLines[0]));
        bool newEmpty = newLines.Length == 0 || (newLines.Length == 1 && string.IsNullOrEmpty(newLines[0]));
        if (oldEmpty) return (newEmpty ? (0, 0) : (newLines.Length, 0));
        if (newEmpty) return (0, oldLines.Length);

        return MyersDiff(oldLines, newLines);
    }

    /// <summary>
    /// Myers diff algorithm (Eugene W. Myers, 1986).
    /// Computes the shortest edit script between two line sequences.
    /// Time: O((N+M)·D)  Space: O((N+M)·D) for the trace used in backtracking.
    /// </summary>
    private static (int Added, int Removed) MyersDiff(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;
        int max = n + m;
        if (max == 0) return (0, 0);

        int offset = max;
        var v = new int[2 * max + 1];
        var trace = new List<int[]>(max + 1);

        for (int d = 0; d <= max; d++)
        {
            trace.Add((int[])v.Clone());

            for (int k = -d; k <= d; k += 2)
            {
                int x;
                bool downMove = k == -d || (k != d && v[k - 1 + offset] < v[k + 1 + offset]);
                x = downMove ? v[k + 1 + offset] : v[k - 1 + offset] + 1;

                int y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }

                v[k + offset] = x;

                if (x >= n && y >= m)
                    return Backtrack(trace, a, b, offset);
            }
        }
        return (m, n); // full replacement fallback (should not be reached)
    }

    private static (int Added, int Removed) Backtrack(
        List<int[]> trace, string[] a, string[] b, int offset)
    {
        int added = 0, removed = 0;
        int x = a.Length, y = b.Length;

        for (int d = trace.Count - 1; d >= 1; d--)
        {
            var v = trace[d];
            int k = x - y;

            bool downMove = k == -d || (k != d && v[k - 1 + offset] < v[k + 1 + offset]);
            int prevK = downMove ? k + 1 : k - 1;

            int prevX = v[prevK + offset];
            int prevY = prevX - prevK;

            // Skip over diagonal (matched) lines
            while (x > prevX + (downMove ? 0 : 1) && y > prevY + (downMove ? 1 : 0))
            { x--; y--; }

            if (downMove) { added++;   y--; }   // insertion (moved down into new file)
            else          { removed++; x--; }   // deletion  (moved right in old file)

            x = prevX;
            y = prevY;
        }
        return (added, removed);
    }

    public string AddedLinesDisplay => AddedLineCount > 0 ? $"+{AddedLineCount}" : string.Empty;
    public string RemovedLinesDisplay => RemovedLineCount > 0 ? $"−{RemovedLineCount}" : string.Empty;

    public string StatusGlyph
    {
        get
        {
            if (NeedsApproval) return "!";
            if (CommandStatus != null) return CommandStatus == "running" ? "•" : IsErrorExitCode ? "×" : "✓";
            if (StatusText.IndexOf("reject", StringComparison.OrdinalIgnoreCase) >= 0 ||
                StatusText.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                ResultText.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                return "×";
            if (HasResult || StatusText.IndexOf("complete", StringComparison.OrdinalIgnoreCase) >= 0 ||
                StatusText.IndexOf("success", StringComparison.OrdinalIgnoreCase) >= 0)
                return "✓";
            return "•";
        }
    }

    public Brush OperationBrush => ResolveBrush(
        IsFailure ? "ErrorRedBrush" :
        ToolName == "__result__" ? "SuccessGreenBrush" :
        IsSchemaOrDataOperation ? "DataOperationBrush" :
        IsShellExecution ? "TerminalOperationBrush" :
        IsFileOperation ? "ReadOperationBrush" :
        "TerminalOperationBrush",
        Brushes.MediumPurple);

    public Brush StatusBrush => ResolveBrush(
        StatusGlyph == "×" ? "ErrorRedBrush" :
        StatusGlyph == "✓" ? "SuccessGreenBrush" :
        NeedsApproval ? "ErrorRedBrush" :
        "TextTertiaryBrush",
        Brushes.Gray);

    private ICommand? _approveCmd;
    public ICommand? ApproveCmd
    {
        get => _approveCmd;
        set { _approveCmd = value; OnPropertyChanged(); }
    }

    private ICommand? _rejectCmd;
    public ICommand? RejectCmd
    {
        get => _rejectCmd;
        set { _rejectCmd = value; OnPropertyChanged(); }
    }

    private ICommand? _alwaysAllowCmd;
    public ICommand? AlwaysAllowCmd
    {
        get => _alwaysAllowCmd;
        set { _alwaysAllowCmd = value; OnPropertyChanged(); }
    }

    /// <summary>Applies a completed tool result with one presentation refresh.</summary>
    public void Complete(string resultText, string statusText)
    {
        BeginPresentationUpdate();
        try
        {
            ResultText = resultText;
            NeedsApproval = false;
            StatusText = statusText;
        }
        finally
        {
            EndPresentationUpdate();
        }
    }

    private void BeginPresentationUpdate() => _presentationUpdateDepth++;

    private void EndPresentationUpdate()
    {
        if (--_presentationUpdateDepth != 0 || !_presentationRefreshPending) return;
        _presentationRefreshPending = false;
        RefreshPresentation();
    }

    /// <summary>
    /// Returns true only for a completed, non-mutating final entry with the same normalized invocation.
    /// This deliberately limits grouping to consecutive retries and leaves approvals and file edits auditable.
    /// </summary>
    public bool CanCollapseWith(string toolName, string parameters)
    {
        if (!IsTerminal || IsFileMutation || NeedsApproval ||
            !string.Equals(ToolName, toolName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var previousParameters = NormalizeParameters(Parameters);
        var incomingParameters = NormalizeParameters(parameters);
        return previousParameters.Length == 0 || incomingParameters.Length == 0 ||
               string.Equals(previousParameters, incomingParameters, StringComparison.Ordinal);
    }

    /// <summary>Retains the completed call as history, then turns this card into its next retry.</summary>
    public void BeginRetry(string id, string parameters, DateTime occurredAt)
    {
        BeginPresentationUpdate();
        try
        {
            _previousAttempts.Add(new ToolAttempt(OccurredAt, StatusText, Parameters, ResultText));
            Id = id;
            Parameters = parameters;
            OccurredAt = occurredAt;
            NeedsApproval = false;
            ResultText = string.Empty;
            StatusText = "Running…";
            OnPropertyChanged(nameof(AttemptCount));
            OnPropertyChanged(nameof(HasAttemptHistory));
            OnPropertyChanged(nameof(AttemptHistory));
        }
        finally
        {
            EndPresentationUpdate();
        }
    }

    private static string NormalizeParameters(string parameters) => string.IsNullOrWhiteSpace(parameters)
        ? string.Empty
        : new string(parameters.Where(character => !char.IsWhiteSpace(character)).ToArray());

    public static ToolCallViewModel Create(string id, string toolName, IDictionary<string, object?>? arguments)
    {
        return new ToolCallViewModel
        {
            Id = id,
            ToolName = toolName,
            Parameters = arguments == null || arguments.Count == 0
                ? string.Empty
                : JsonSerializer.Serialize(arguments, new JsonSerializerOptions { WriteIndented = true }),
            StatusText = "Running…"
        };
    }

    public static ToolCallViewModel CreateResult(string result)
    {
        return new ToolCallViewModel
        {
            Id = Guid.NewGuid().ToString(),
            ToolName = "__result__",
            Parameters = string.Empty,
            ResultText = result,
            StatusText = "Completed"
        };
    }

    private void RebuildArgumentCache()
    {
        _arguments.Clear();
        _hasParsedArguments = false;
        _rawRemovedText = string.Empty;
        _rawAddedText = string.Empty;

        if (string.IsNullOrWhiteSpace(Parameters)) return;

        try
        {
            using var document = JsonDocument.Parse(Parameters);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                _arguments[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.ToString();
            }

            _hasParsedArguments = true;
        }
        catch (JsonException)
        {
            // Keep raw parameter text available for display; it has no structured target.
        }

        if (!IsFileMutation) return;

        // RemovedText raw extraction
        var removedRaw = GetArgument("oldText") ?? GetArgument("TargetContent");
        if (string.IsNullOrEmpty(removedRaw))
        {
            var chunksJson = GetArgument("ReplacementChunks");
            if (!string.IsNullOrEmpty(chunksJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(chunksJson);
                    var sb = new System.Text.StringBuilder();
                    foreach (var chunk in doc.RootElement.EnumerateArray())
                    {
                        if (chunk.TryGetProperty("TargetContent", out var tc) || chunk.TryGetProperty("targetContent", out tc))
                        {
                            if (sb.Length > 0) sb.AppendLine();
                            sb.Append(tc.GetString());
                        }
                    }
                    removedRaw = sb.ToString();
                }
                catch { }
            }
            else
            {
                var editsJson = GetArgument("edits");
                if (!string.IsNullOrEmpty(editsJson))
                {
                    removedRaw = ExtractFromEdits(editsJson, "oldText");
                }
                else
                {
                    var filesJson = GetArgument("files");
                    if (!string.IsNullOrEmpty(filesJson))
                    {
                        removedRaw = ExtractFromFilesEdits(filesJson, "oldText");
                    }
                }
            }
        }
        _rawRemovedText = removedRaw ?? string.Empty;

        // AddedText raw extraction
        var addedRaw = GetArgument("newText") ?? GetArgument("content") ?? GetArgument("ReplacementContent") ?? GetArgument("CodeContent");
        if (string.IsNullOrEmpty(addedRaw))
        {
            var chunksJson = GetArgument("ReplacementChunks");
            if (!string.IsNullOrEmpty(chunksJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(chunksJson);
                    var sb = new System.Text.StringBuilder();
                    foreach (var chunk in doc.RootElement.EnumerateArray())
                    {
                        if (chunk.TryGetProperty("ReplacementContent", out var rc) || chunk.TryGetProperty("replacementContent", out rc))
                        {
                            if (sb.Length > 0) sb.AppendLine();
                            sb.Append(rc.GetString());
                        }
                    }
                    addedRaw = sb.ToString();
                }
                catch { }
            }
            else
            {
                var editsJson = GetArgument("edits");
                if (!string.IsNullOrEmpty(editsJson))
                {
                    addedRaw = ExtractFromEdits(editsJson, "newText");
                }
                else
                {
                    var filesJson = GetArgument("files");
                    if (!string.IsNullOrEmpty(filesJson))
                    {
                        addedRaw = ExtractFromFilesEdits(filesJson, "newText");
                    }
                }
            }
        }
        _rawAddedText = addedRaw ?? string.Empty;
    }

    private string? GetArgument(string key)
    {
        return _hasParsedArguments && _arguments.TryGetValue(key, out var value) ? value : null;
    }

    private static string? ExtractFromEdits(string editsJson, string propertyName)
    {
        try
        {
            using var doc = JsonDocument.Parse(editsJson);
            var sb = new System.Text.StringBuilder();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var edit in doc.RootElement.EnumerateArray())
                {
                    if (edit.TryGetProperty(propertyName, out var prop))
                    {
                        if (sb.Length > 0) sb.AppendLine();
                        sb.Append(prop.GetString());
                    }
                }
            }
            return sb.ToString();
        }
        catch { return null; }
    }

    private static string? ExtractFromFilesEdits(string filesJson, string propertyName)
    {
        try
        {
            using var doc = JsonDocument.Parse(filesJson);
            var sb = new System.Text.StringBuilder();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in doc.RootElement.EnumerateArray())
                {
                    if (file.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var edit in edits.EnumerateArray())
                        {
                            if (edit.TryGetProperty(propertyName, out var prop))
                            {
                                if (sb.Length > 0) sb.AppendLine();
                                sb.Append(prop.GetString());
                            }
                        }
                    }
                }
            }
            return sb.ToString();
        }
        catch { return null; }
    }

    private static string ShortenPath(string path)
    {
        var normalized = path.Replace('/', '\\').TrimEnd('\\');
        var separator = normalized.LastIndexOf('\\');
        return separator >= 0 ? normalized.Substring(separator + 1) : normalized;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
        return value.Substring(0, maxLength).TrimEnd() + "\n…";
    }

    private static int CountLines(string value) => string.IsNullOrWhiteSpace(value) ? 0 : value.Count(character => character == '\n') + 1;

    private static Brush ResolveBrush(string resourceKey, Brush fallback)
    {
        return Application.Current?.TryFindResource(resourceKey) as Brush ?? fallback;
    }

    private void RefreshPresentation()
    {
        if (_presentationUpdateDepth > 0)
        {
            _presentationRefreshPending = true;
            return;
        }

        foreach (var property in new[]
        {
            nameof(AbsolutePath), nameof(HasParameters), nameof(OperationIconKey), nameof(OperationLabel), nameof(Target), nameof(ResultSummary),
            nameof(HasResultSummary), nameof(ParameterDetails), nameof(HasDisplayParameters), nameof(HeaderSummary), nameof(HasDetails), nameof(HasChangePreview), nameof(IsFileMutation),
            nameof(IsFileOperation), nameof(IsSchemaOrDataOperation), nameof(IsShellExecution), nameof(IsFailure),
            nameof(AttemptCount), nameof(HasAttemptHistory), nameof(AttemptHistory),
            nameof(RemovedText), nameof(AddedText), nameof(RemovedLineCount), nameof(AddedLineCount),
            nameof(AddedLinesDisplay), nameof(RemovedLinesDisplay),
            nameof(StatusGlyph), nameof(OperationBrush), nameof(StatusBrush), nameof(IsRunning),
            nameof(IsReadFiles), nameof(IsSearchCodebase), nameof(IsListFiles), nameof(HasCustomView), nameof(IsErrorExitCode), nameof(TerminalOutput), nameof(CustomExpandedViewText)
        })
        {
            OnPropertyChanged(property);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string name = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private static JsonDocument? TryParse(string s) { try { return JsonDocument.Parse(s); } catch { return null; } }

    private static string? GetStringSafe(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) return element.GetString();
        if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > 0) return GetStringSafe(element[0]);
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("path", out var pathProp)) return GetStringSafe(pathProp);
            if (element.TryGetProperty("filePath", out var filePathProp)) return GetStringSafe(filePathProp);
        }
        return element.ToString();
    }

    private static string? ExtractFilePath(string filePaths)
    {
        var first = TryExtractFirstFilePathRobust(filePaths, out int len);
        if (!string.IsNullOrWhiteSpace(first))
        {
            return len > 1 ? $"{ShortenPath(first)} + {len - 1} more" : ShortenPath(first);
        }
        return null;
    }

    private static string? TryExtractFirstFilePathRobust(string filePaths, out int count)
    {
        count = 0;
        if (string.IsNullOrWhiteSpace(filePaths)) return null;

        using JsonDocument? doc = TryParse(filePaths);
        if (doc != null && doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
        {
            count = doc.RootElement.GetArrayLength();
            return GetStringSafe(doc.RootElement[0]);
        }

        // Fallback for corrupted double-escaped JSON strings (e.g. from GetString() unescaping backslashes)
        var objectMatches = System.Text.RegularExpressions.Regex.Matches(filePaths, @"\""(?:filePath|path|TargetFile)?\""\s*:\s*\""([^\r\n""]+)\""");
        if (objectMatches.Count > 0)
        {
            count = objectMatches.Count;
            return objectMatches[0].Groups[1].Value;
        }

        var stringMatches = System.Text.RegularExpressions.Regex.Matches(filePaths, @"(?:^|\[|,)\s*\""([^\r\n""]+)\""");
        if (stringMatches.Count > 0)
        {
            count = stringMatches.Count;
            return stringMatches[0].Groups[1].Value;
        }

        var cleanFilePaths = filePaths.Trim('[', ']', '\n', '\r', ' ', '"', '\'');
        count = string.IsNullOrWhiteSpace(cleanFilePaths) ? 0 : 1;
        return count > 0 ? cleanFilePaths : null;
    }
}
