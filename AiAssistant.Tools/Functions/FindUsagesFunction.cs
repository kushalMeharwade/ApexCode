using System;
using System.ComponentModel;
using System.Threading.Tasks;
using AiAssistant.Tools.Services;
using AiAssistant.Core.Services;
using AiAssistant.Engine.Services;
using Microsoft.Extensions.AI;

namespace AiAssistant.Tools.Functions;

public class FindUsagesFunction : IToolProvider
{
    private readonly IRoslynEditService _roslynEditService;
    private readonly IVisualStudioEnvironmentService _vsEnvService;
    private readonly IOutputLogger _outputLogger;

    public FindUsagesFunction(IRoslynEditService roslynEditService, IVisualStudioEnvironmentService vsEnvService, IOutputLogger outputLogger)
    {
        _roslynEditService = roslynEditService;
        _vsEnvService = vsEnvService;
        _outputLogger = outputLogger;
    }

    [Description("Use this when you have a specific file, line, and column (e.g. from a diagnostic or search result).")]
    public AIFunction CreateFunction() => new FindUsagesCustomFunction(_roslynEditService, _vsEnvService, _outputLogger);

    private class FindUsagesCustomFunction : AiAssistant.Tools.Functions.CustomAIFunction
    {
        private readonly IRoslynEditService _roslynEditService;
        private readonly IVisualStudioEnvironmentService _vsEnvService;
        private readonly IOutputLogger _outputLogger;

        public FindUsagesCustomFunction(IRoslynEditService roslynEditService, IVisualStudioEnvironmentService vsEnvService, IOutputLogger outputLogger)
            : base("find_references_at_cursor",
                   "Use this when you have a specific file, line, and column (e.g. from a diagnostic or search result).",
                   @"{
                        ""type"": ""object"",
                        ""properties"": {
                            ""filePath"": { ""type"": ""string"", ""description"": ""The path to the C# file, relative to workspace root. Use forward slashes."" },
                            ""line"": { ""type"": ""integer"", ""description"": ""The 1-based line number where the symbol is located."" },
                            ""column"": { ""type"": ""integer"", ""description"": ""The 1-based column number where the symbol starts."" }
                        },
                        ""required"": [""filePath"", ""line"", ""column""]
                   }")
        {
            _roslynEditService = roslynEditService;
            _vsEnvService = vsEnvService;
            _outputLogger = outputLogger;
        }

        protected override async Task<object> InvokeCoreImplAsync(System.Collections.Generic.IReadOnlyDictionary<string, object> arguments, System.Threading.CancellationToken cancellationToken)
        {
            if (!arguments.TryGetValue("filePath", out var fpObj) || fpObj?.ToString() is not string filePath)
                return "Error: filePath argument is missing.";

            int line = 0;
            if (arguments.TryGetValue("line", out var lnObj))
            {
                if (lnObj is System.Text.Json.JsonElement elemLn && elemLn.ValueKind == System.Text.Json.JsonValueKind.Number)
                    line = elemLn.GetInt32();
                else if (lnObj is IConvertible conv)
                    line = conv.ToInt32(null);
            }

            int column = 0;
            if (arguments.TryGetValue("column", out var colObj))
            {
                if (colObj is System.Text.Json.JsonElement elemCol && elemCol.ValueKind == System.Text.Json.JsonValueKind.Number)
                    column = elemCol.GetInt32();
                else if (colObj is IConvertible conv)
                    column = conv.ToInt32(null);
            }

            await _outputLogger.LogAsync(LogCategory.Tool, $"► find_references_at_cursor(filePath=\"{filePath}\", line={line}, column={column})");

            string absolutePath;
            try
            {
                var workspaceRoot = await _vsEnvService.GetWorkspaceRootAsync(cancellationToken);
                absolutePath = WorkspacePathResolver.ResolveWorkspacePath(filePath, workspaceRoot);
            }
            catch (Exception ex)
            {
                await _outputLogger.LogAsync(LogCategory.Tool, $"◄ find_references_at_cursor → Error: Path resolution failed for {filePath}");
                return $"Error: Path resolution failed or traversal denied. {ex.Message}";
            }

            try
            {
                if (!System.IO.File.Exists(absolutePath))
                {
                    await _outputLogger.LogAsync(LogCategory.Tool, $"◄ find_references_at_cursor → Error: File not found");
                    return $"File not found: {WorkspacePathResolver.ToRelativePath(absolutePath, await _vsEnvService.GetWorkspaceRootAsync(cancellationToken))}";
                }

                var usages = await _roslynEditService.FindUsagesAsync(absolutePath, line, column);
                var usagesList = new System.Collections.Generic.List<string>(usages);
                
                if (usagesList.Count == 0)
                {
                    await _outputLogger.LogAsync(LogCategory.Tool, $"◄ find_references_at_cursor → 0 usages found");
                    return "No usages found for the symbol at this location.";
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"## Usages Found ({usagesList.Count})");
                foreach (var usage in usagesList)
                {
                    sb.AppendLine($"- {usage}");
                }
                _outputLogger.Log(LogCategory.Tool, $"◄ find_references_at_cursor → {usagesList.Count} usages found", detail: sb.ToString());
                return sb.ToString();
            }
            catch (Exception ex)
            {
                _outputLogger.Log(LogCategory.Tool, $"◄ find_references_at_cursor → Error: {ex.Message}");
                return $"Error finding usages: {ex.Message}";
            }
        }
    }
}



