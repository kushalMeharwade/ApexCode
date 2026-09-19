using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using AiAssistant.Tools.Services;
using AiAssistant.Core.Services;
using Microsoft.Extensions.AI;

namespace AiAssistant.Tools.Functions;

public class ListWorkspaceFilesFunction : IToolProvider
{
    private readonly IVisualStudioEnvironmentService _vsEnvService;
    private readonly ISettingsService _settingsService;
    private readonly IOutputLogger? _outputLogger;

    public ListWorkspaceFilesFunction(IVisualStudioEnvironmentService vsEnvService, ISettingsService settingsService, IOutputLogger? outputLogger = null)
    {
        _vsEnvService = vsEnvService;
        _settingsService = settingsService;
        _outputLogger = outputLogger;
    }

    [Description("Lists all source files in the active Visual Studio workspace solution, returning their full paths. Useful for @-mention file picking.")]
    public AIFunction CreateFunction() => new ListWorkspaceFilesCustomFunction(_vsEnvService, _settingsService, _outputLogger);

    private class ListWorkspaceFilesCustomFunction : CustomAIFunction
    {
        private readonly IVisualStudioEnvironmentService _vsEnvService;
        private readonly ISettingsService _settingsService;
        private readonly IOutputLogger? _outputLogger;

        public ListWorkspaceFilesCustomFunction(IVisualStudioEnvironmentService vsEnvService, ISettingsService settingsService, IOutputLogger? outputLogger)
            : base("list_all_solution_files",
                   "Returns all source file paths in the active Visual Studio solution.",
                   @"{
                       ""type"": ""object"",
                       ""properties"": {
                           ""includeExtensions"": { ""type"": ""string"", ""description"": ""Comma-separated list of extensions to include (e.g. '.cs,.js,.py'). Defaults to all source extensions."" }
                       }
                   }")
        {
            _vsEnvService = vsEnvService;
            _settingsService = settingsService;
            _outputLogger = outputLogger;
        }

        protected override async Task<object> InvokeCoreImplAsync(IReadOnlyDictionary<string, object> arguments, System.Threading.CancellationToken cancellationToken)
        {
            string? includeExtensions = null;
            if (arguments.TryGetValue("includeExtensions", out var extObj) && extObj?.ToString() is string ext)
                includeExtensions = ext;

            _outputLogger?.Log(LogCategory.Tool, $"► {Name}(extensions={includeExtensions ?? "all"})");

            try
            {
                var workspacePath = await _vsEnvService.GetWorkspaceRootAsync(cancellationToken);
                if (string.IsNullOrEmpty(workspacePath))
                {
                    return "Error: No workspace is open.";
                }

                var directory = workspacePath;
                if (!Directory.Exists(directory))
                {
                    return new List<string>();
                }

                var extensionsStr = string.IsNullOrEmpty(includeExtensions) ? _settingsService.WorkspaceExtensions : includeExtensions;
                var extensions = extensionsStr.Split(',').Select(e => e.Trim()).Where(e => !string.IsNullOrEmpty(e)).ToArray();

                var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                    .Where(f => extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                    .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && !f.Contains("\\node_modules\\") && !f.Contains("\\.git\\") && !f.Contains("\\__pycache__\\"))
                    .OrderBy(f => f)
                    .ToList();

                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → {files.Count} files found");
                return files;
            }
            catch (System.Exception ex)
            {
                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → Error: {ex.Message}");
                return new List<string> { $"Error: {ex.Message}" };
            }
        }
    }
}


