using System;
using System.ComponentModel;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Collections.Generic;
using AiAssistant.Tools.Services;
using AiAssistant.Core.Services;
using Microsoft.Extensions.AI;

namespace AiAssistant.Tools.Functions;

public class GetDiagnosticsFunction : IToolProvider
{
    private readonly IVisualStudioEnvironmentService _vsEnvService;
    private readonly IOutputLogger? _outputLogger;

    public GetDiagnosticsFunction(IVisualStudioEnvironmentService vsEnvService, IOutputLogger? outputLogger = null)
    {
        _vsEnvService = vsEnvService;
        _outputLogger = outputLogger;
    }

    [Description("Gets compiler errors and warnings directly from the active Visual Studio Error List.")]
    public AIFunction CreateFunction() => new GetDiagnosticsCustomFunction(_vsEnvService, _outputLogger);

    private class GetDiagnosticsCustomFunction : CustomAIFunction
    {
        private readonly IVisualStudioEnvironmentService _vsEnvService;
        private readonly IOutputLogger? _outputLogger;

        public GetDiagnosticsCustomFunction(IVisualStudioEnvironmentService vsEnvService, IOutputLogger? outputLogger)
            : base("get_diagnostics",
                   "Reads current compiler errors and warnings directly from the Visual Studio Error List.",
                    @"{
                        ""type"": ""object"",
                        ""properties"": {
                            ""severity"": { ""type"": ""string"", ""description"": ""'error', 'warning', or 'all'"" },
                            ""filePath"": { ""type"": ""string"", ""description"": ""Optional. Filter diagnostics to a specific file path, relative to workspace root. Use forward slashes."" }
                        }
                   }")
        {
            _vsEnvService = vsEnvService;
            _outputLogger = outputLogger;
        }

        protected override async Task<object> InvokeCoreImplAsync(IReadOnlyDictionary<string, object> arguments, CancellationToken cancellationToken)
        {
            string severity = "all";
            string? filePath = null;
            if (arguments.TryGetValue("severity", out var sevObj) && sevObj?.ToString() is string s)
                severity = s;
            if (arguments.TryGetValue("filePath", out var pathObj) && pathObj?.ToString() is string p)
                filePath = p;
            var fpDisplay = filePath ?? "null";
            _outputLogger?.Log(LogCategory.Tool, $"► {Name}(severity={severity}, filePath={fpDisplay})");

            try
            {
                var diagnostics = (await _vsEnvService.GetErrorListDiagnosticsAsync(severity)).ToList();

                if (!string.IsNullOrWhiteSpace(filePath))
                {
                    var canonicalPath = await ResolveCanonicalFilePathAsync(filePath);
                    if (!File.Exists(canonicalPath))
                    {
                        return $"File not found: '{filePath}' (resolved to '{canonicalPath}'). Ensure the file exists in the workspace.";
                    }
                    
                    diagnostics = diagnostics.Where(d => string.Equals(d.FileName, canonicalPath, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                if (diagnostics.Count == 0)
                    return $"No {severity} diagnostics found in the Visual Studio Error List{(string.IsNullOrWhiteSpace(filePath) ? "" : $" for '{filePath}'")}.";

                var sb = new StringBuilder();
                sb.AppendLine($"## Diagnostics ({diagnostics.Count} entries)");
                sb.AppendLine();
                foreach (var entry in diagnostics.Take(50))
                {
                    sb.AppendLine($"- [{entry.Severity}] {entry.FileName}({entry.Line},{entry.Column}): {entry.Description}");
                }
                if (diagnostics.Count > 50)
                    sb.AppendLine($"\n... and {diagnostics.Count - 50} more entries.");

                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → {diagnostics.Count} {severity} diagnostics found");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → Error: {ex.Message}");
                return $"Error getting diagnostics: {ex.Message}";
            }
        }

        private async Task<string> ResolveCanonicalFilePathAsync(string filePath)
        {
            var workspace = await _vsEnvService.GetWorkspaceAsync();
            if (workspace != null)
            {
                var normalizedSuffix = Path.DirectorySeparatorChar + filePath.Replace('/', '\\');
                var matchedDoc = workspace.CurrentSolution.Projects
                    .SelectMany(p => p.Documents.Concat(p.AdditionalDocuments))
                    .FirstOrDefault(d => d.FilePath != null && 
                        (string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase) || 
                         d.FilePath.EndsWith(normalizedSuffix, StringComparison.OrdinalIgnoreCase)));

                if (matchedDoc != null && matchedDoc.FilePath != null)
                {
                    return matchedDoc.FilePath;
                }
            }

            var solutionDir = await _vsEnvService.GetWorkspaceRootAsync(CancellationToken.None);
            var baseDir = string.IsNullOrEmpty(solutionDir) ? Environment.CurrentDirectory : solutionDir;
            
            try
            {
                return WorkspacePathResolver.ResolveWorkspacePath(filePath, baseDir);
            }
            catch
            {
                return filePath; // Fallback if resolve fails, so we can still try to match FileName
            }
        }
    }
}


