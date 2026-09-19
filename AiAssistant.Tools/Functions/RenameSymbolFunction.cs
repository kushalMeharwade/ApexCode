using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using AiAssistant.Core.Services;
using AiAssistant.Tools.Services;
using System.Text.Json;

namespace AiAssistant.Tools.Functions;

public class RenameSymbolFunction : IToolProvider
{
    private readonly IOutputLogger? _outputLogger;
    private readonly IVisualStudioEnvironmentService _vsEnvService;
    private readonly ITransactionSnapshotService _transactionService;
    private readonly ISettingsService _settingsService;
    private readonly IApprovalService? _approvalService;

    public RenameSymbolFunction(
        IVisualStudioEnvironmentService vsEnvService, 
        ITransactionSnapshotService transactionService,
        ISettingsService settingsService,
        IApprovalService? approvalService = null,
        IOutputLogger? outputLogger = null)
    {
        _vsEnvService = vsEnvService;
        _transactionService = transactionService;
        _settingsService = settingsService;
        _approvalService = approvalService;
        _outputLogger = outputLogger;
    }

    [Description("Renames a code symbol (class, method, variable) across the entire solution using the Roslyn compiler. Highly accurate.")]
    public AIFunction CreateFunction() => new RenameSymbolCustomFunction(_vsEnvService, _transactionService, _settingsService, _approvalService, _outputLogger);

    public delegate Task<bool> PreviewResolverDelegate(Dictionary<string, string> originalContents, Dictionary<string, string> newContents);
    public static PreviewResolverDelegate? PreviewResolver { get; set; }

    private class RenameSymbolCustomFunction : CustomAIFunction
    {
        private readonly IVisualStudioEnvironmentService _vsEnvService;
        private readonly ITransactionSnapshotService _transactionService;
        private readonly ISettingsService _settingsService;
        private readonly IApprovalService? _approvalService;
        private readonly IOutputLogger? _outputLogger;

        public RenameSymbolCustomFunction(
            IVisualStudioEnvironmentService vsEnvService,
            ITransactionSnapshotService transactionService,
            ISettingsService settingsService,
            IApprovalService? approvalService,
            IOutputLogger? outputLogger)
            : base("rename_symbol",
                   "Renames a code symbol across the entire solution.",
                   @"{
                        ""type"": ""object"",
                        ""properties"": {
                            ""filePath"": { ""type"": ""string"", ""description"": ""The path to the file containing the symbol to rename, relative to workspace root. Use forward slashes."" },
                            ""line"": { ""type"": ""integer"", ""description"": ""The 1-based line number where the symbol is located."" },
                            ""symbolName"": { ""type"": ""string"", ""description"": ""The current name of the symbol."" },
                            ""newName"": { ""type"": ""string"", ""description"": ""The new name for the symbol."" },
                            ""dryRun"": { ""type"": ""boolean"", ""description"": ""Optional. If true, returns the files that would be modified without actually changing them."" }
                        },
                        ""required"": [""filePath"", ""line"", ""symbolName"", ""newName""]
                   }")
        {
            _vsEnvService = vsEnvService;
            _transactionService = transactionService;
            _settingsService = settingsService;
            _approvalService = approvalService;
            _outputLogger = outputLogger;
        }

        protected override async Task<object?> InvokeCoreImplAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
        {
            var filePath = arguments["filePath"]?.ToString();
            var lineObj = arguments["line"];
            int lineNum = lineObj != null ? System.Convert.ToInt32(lineObj.ToString()) : 1;
            var symbolName = arguments["symbolName"]?.ToString();
            var newName = arguments["newName"]?.ToString();
            var dryRunObj = arguments.TryGetValue("dryRun", out var d) ? d : null;
            bool dryRun = dryRunObj != null && System.Convert.ToBoolean(dryRunObj.ToString());

            if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(symbolName) || string.IsNullOrEmpty(newName))
            {
                return new { status = "failed", error = "Missing required parameters: filePath, symbolName, or newName." };
            }

            _outputLogger?.Log(LogCategory.Tool, $"► {Name}({symbolName} -> {newName} at {filePath}:{lineNum})");

            // Check approval mode
            var approvalMode = _settingsService.GetToolApprovalMode("rename_symbol");
            if (approvalMode == "denyall")
            {
                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → Error: Tool execution rejected due to Deny All setting.");
                return new { status = "failed", error = "Error: User rejected tool execution." };
            }

            if (_approvalService != null && approvalMode != "allowall")
            {
                var req = await _approvalService.RequestApprovalAsync(Name, $"Rename '{symbolName}' to '{newName}'", arguments.ToDictionary(k => k.Key, v => (object?)v.Value));
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

            var workspace = await _vsEnvService.GetWorkspaceAsync();
            if (workspace == null)
            {
                return new { status = "failed", error = "No active Roslyn Workspace available." };
            }

            var workspaceRoot = await _vsEnvService.GetWorkspaceRootAsync(cancellationToken);
            if (string.IsNullOrEmpty(workspaceRoot))
            {
                return new { status = "failed", error = "No active solution directory." };
            }

            string absolutePath;
            try
            {
                absolutePath = WorkspacePathResolver.ResolveWorkspacePath(filePath, workspaceRoot);
            }
            catch (Exception ex)
            {
                return new { status = "failed", error = $"Path resolution failed: {ex.Message}" };
            }

            // Find Document
            var document = workspace.CurrentSolution.Projects
                .SelectMany(p => p.Documents)
                .FirstOrDefault(d => string.Equals(d.FilePath, absolutePath, System.StringComparison.OrdinalIgnoreCase));

            if (document == null)
            {
                return new { status = "failed", error = $"File not found in solution: {WorkspacePathResolver.ToRelativePath(absolutePath, workspaceRoot)}" };
            }

            // Compute Absolute Position
            if (!System.IO.File.Exists(absolutePath))
            {
                return new { status = "failed", error = $"File not found on disk: {WorkspacePathResolver.ToRelativePath(absolutePath, workspaceRoot)}" };
            }

            string[] lines = System.IO.File.ReadAllLines(absolutePath);
            if (lineNum < 1 || lineNum > lines.Length)
            {
                return new { status = "failed", error = $"Line number {lineNum} is out of bounds." };
            }

            string targetLine = lines[lineNum - 1];
            int localIndex = targetLine.IndexOf(symbolName);
            if (localIndex < 0)
            {
                return new { status = "failed", error = $"Symbol '{symbolName}' not found on line {lineNum}." };
            }

            // Read file chars up to this line to compute exact offset, respecting specific line endings
            string fileContent = System.IO.File.ReadAllText(absolutePath);
            int absolutePosition = 0;
            int currentLine = 1;
            int ptr = 0;
            while (ptr < fileContent.Length && currentLine < lineNum)
            {
                if (fileContent[ptr] == '\n')
                {
                    currentLine++;
                }
                else if (fileContent[ptr] == '\r' && ptr + 1 < fileContent.Length && fileContent[ptr + 1] == '\n')
                {
                    ptr++;
                    currentLine++;
                }
                ptr++;
            }
            absolutePosition = ptr + localIndex;

            // Semantic Model and Symbol
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel == null)
            {
                return new { status = "failed", error = "Failed to retrieve SemanticModel." };
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var node = root?.FindToken(absolutePosition).Parent;
            if (node == null)
            {
                return new { status = "failed", error = "SyntaxNode not found at calculated position." };
            }

            var symbol = Microsoft.CodeAnalysis.FindSymbols.SymbolFinder.FindSymbolAtPositionAsync(semanticModel, absolutePosition, workspace, cancellationToken).Result;
            if (symbol == null)
            {
                // Fallback to GetDeclaredSymbol or GetSymbolInfo
                symbol = semanticModel.GetDeclaredSymbol(node, cancellationToken);
                if (symbol == null)
                {
                    symbol = semanticModel.GetSymbolInfo(node, cancellationToken).Symbol;
                }
                if (symbol == null)
                {
                    return new { status = "failed", error = $"Could not resolve symbol '{symbolName}' at position {absolutePosition}." };
                }
            }

            // Perform Rename
            var newSolution = await Microsoft.CodeAnalysis.Rename.Renamer.RenameSymbolAsync(
                workspace.CurrentSolution, symbol, newName, workspace.Options, cancellationToken);

            // Extract changes from the solution diff
            var solutionChanges = newSolution.GetChanges(workspace.CurrentSolution);
            var modifiedProjects = solutionChanges.GetProjectChanges().ToList();
            
            var originalContents = new Dictionary<string, string>();
            var newContents = new Dictionary<string, string>();

            foreach (var projChange in modifiedProjects)
            {
                foreach (var docId in projChange.GetChangedDocuments())
                {
                    var oldDoc = workspace.CurrentSolution.GetDocument(docId);
                    var newDoc = newSolution.GetDocument(docId);
                    
                    if (oldDoc?.FilePath != null && newDoc != null)
                    {
                        var oldText = await oldDoc.GetTextAsync(cancellationToken);
                        var newText = await newDoc.GetTextAsync(cancellationToken);
                        
                        originalContents[oldDoc.FilePath] = oldText.ToString();
                        newContents[oldDoc.FilePath] = newText.ToString();
                    }
                }
            }

            if (originalContents.Count == 0)
            {
                return new { status = "success", message = $"No changes required for renaming '{symbolName}' to '{newName}'." };
            }

            // Dry run: generate unified diffs
            if (dryRun)
            {
                var diffs = new List<object>();
                foreach (var kvp in newContents)
                {
                    diffs.Add(new 
                    { 
                        file = WorkspacePathResolver.ToRelativePath(kvp.Key, workspaceRoot), 
                        unifiedDiff = ComputeMinimalDiff(originalContents[kvp.Key], kvp.Value) 
                    });
                }
                
                return new 
                { 
                    status = "dry_run", 
                    message = $"Renaming '{symbolName}' to '{newName}' would modify {originalContents.Count} file(s).",
                    diffs = diffs
                };
            }

            // PreviewResolver / Approval Hook
            if (PreviewResolver != null)
            {
                bool approved = await PreviewResolver(originalContents, newContents);
                if (!approved)
                {
                    return "USER REJECTED THIS ACTION. CRITICAL INSTRUCTION: Do NOT retry this tool. Do NOT attempt alternative commands. You MUST STOP and ask the user for further instructions.";
                }
            }

            // Diagnostics Hook (Before)
            var preDiagnostics = (await _vsEnvService.GetErrorListDiagnosticsAsync("error"))
                .Select(d => $"[{d.Severity}] {d.FileName}({d.Line},{d.Column}): {d.Description}")
                .ToList();

            // Transactional Write (via transaction system, NOT workspace.TryApplyChanges)
            var transactionId = _transactionService.BeginTransaction(workspaceRoot, originalContents);

            try
            {
                foreach (var kvp in newContents)
                {
                    if (originalContents[kvp.Key] != kvp.Value)
                    {
                        await AiAssistant.Storage.SafeFileWriter.WriteAllTextAsync(kvp.Key, kvp.Value);
                    }
                }
            }
            catch (System.Exception ex)
            {
                await _transactionService.RevertTransactionAsync(workspaceRoot, transactionId);
                return new { status = "failed", error = $"Write failed, changes rolled back. Exception: {ex.Message}" };
            }

            // Diagnostics Hook (After) - Await WorkspaceChanged or Timeout
            var tcs = new TaskCompletionSource<bool>();
            System.EventHandler? workspaceChangedHandler = (s, e) => { tcs.TrySetResult(true); };
            
            _vsEnvService.WorkspaceChanged += workspaceChangedHandler;
            try
            {
                // Give Roslyn a chance to trigger WorkspaceChanged (max 800ms)
                using var cts = new CancellationTokenSource(800);
                cts.Token.Register(() => tcs.TrySetResult(false));
                await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                _vsEnvService.WorkspaceChanged -= workspaceChangedHandler;
            }

            var postDiagnostics = (await _vsEnvService.GetErrorListDiagnosticsAsync("error"))
                .Select(d => $"[{d.Severity}] {d.FileName}({d.Line},{d.Column}): {d.Description}")
                .ToList();
            var newDiagnostics = postDiagnostics.Except(preDiagnostics).ToList();

            // Commit Transaction
            _transactionService.CompleteTransaction(workspaceRoot, transactionId);

            var msg = $"Successfully renamed '{symbolName}' to '{newName}'. Modified {originalContents.Count} file(s).";
            _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → {msg}");
            
            return new
            {
                status = "success",
                transactionId = transactionId,
                message = msg,
                modifiedFiles = originalContents.Keys.Select(k => WorkspacePathResolver.ToRelativePath(k, workspaceRoot)).ToList(),
                diagnostics = newDiagnostics
            };
        }

        private static string ComputeMinimalDiff(string oldContent, string newContent)
        {
            var oldLines = oldContent.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None);
            var newLines = newContent.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None);
            
            // Simple chunked diff to avoid dumping hundreds of unchanged lines.
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
                    int printStartOld = System.Math.Max(0, i - context);
                    int printEndOld = matchI - 1;
                    int printStartNew = System.Math.Max(0, j - context);
                    int printEndNew = matchJ - 1;
                    
                    diff.AppendLine($"@@ -{printStartOld + 1},{printEndOld - printStartOld + 1} +{printStartNew + 1},{printEndNew - printStartNew + 1} @@");
                    
                    for (int c = printStartOld; c < i; c++) diff.AppendLine($" {oldLines[c]}");
                    for (int c = i; c < matchI; c++) diff.AppendLine($"-{oldLines[c]}");
                    for (int c = j; c < matchJ; c++) diff.AppendLine($"+{newLines[c]}");
                    for (int c = matchI; c < System.Math.Min(oldLines.Length, matchI + context); c++) diff.AppendLine($" {oldLines[c]}");
                }

                if (maxMatch == 0) break;

                i = matchI + maxMatch;
                j = matchJ + maxMatch;
            }

            return diff.ToString();
        }
    }
}


