using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AiAssistant.Tools.Services;
using AiAssistant.Core.Services;

namespace AiAssistant.Tools.Functions;

public class ListFilesFunction : IToolProvider
{
    private readonly IVisualStudioEnvironmentService _vsEnvService;
    private readonly IOutputLogger? _outputLogger;

    public ListFilesFunction(IVisualStudioEnvironmentService vsEnvService, IOutputLogger? outputLogger = null)
    {
        _vsEnvService = vsEnvService;
        _outputLogger = outputLogger;
    }

    [Description("Lists all files in a directory matching the specified pattern within the workspace.")]
    public AIFunction CreateFunction() => new ListFilesCustomFunction(_vsEnvService, _outputLogger);

    private class ListFilesCustomFunction : CustomAIFunction
    {
        private readonly IVisualStudioEnvironmentService _vsEnvService;
        private readonly IOutputLogger? _outputLogger;

        public ListFilesCustomFunction(IVisualStudioEnvironmentService vsEnvService, IOutputLogger? outputLogger)
            : base("list_files",
                   "Lists all files in a directory within the active Visual Studio workspace.",
                   @"{
                        ""type"": ""object"",
                        ""properties"": {
                            ""directory"": { ""type"": ""string"", ""description"": ""The directory path to search. Use '.' for current solution directory."" },
                            ""pattern"": { ""type"": ""string"", ""description"": ""Optional search pattern (e.g., '*.cs')."" },
                            ""recursive"": { ""type"": ""boolean"", ""description"": ""If true, searches all subdirectories. Default is false."" }
                        }
                   }")
        {
            _vsEnvService = vsEnvService;
            _outputLogger = outputLogger;
        }

        protected override async Task<object> InvokeCoreImplAsync(IReadOnlyDictionary<string, object> arguments, System.Threading.CancellationToken cancellationToken)
        {
            string directory = ".";
            if (arguments.TryGetValue("directory", out var dObj) && dObj?.ToString() is string d)
                directory = d;

            string pattern = "*";
            if (arguments.TryGetValue("pattern", out var pObj) && pObj?.ToString() is string p)
                pattern = p;

            bool recursive = false;
            if (arguments.TryGetValue("recursive", out var rObj) && rObj is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.True)
                recursive = true;
            else if (rObj is bool b)
                recursive = b;
                
            _outputLogger?.Log(LogCategory.Tool, $"► {Name}({directory}, {pattern}, recursive={recursive})");

            var workspacePath = await _vsEnvService.GetWorkspaceRootAsync(cancellationToken);
            if (string.IsNullOrEmpty(workspacePath)) return "Error: No workspace is open.";
            var rootDir = workspacePath;
            var targetDir = directory == "." ? rootDir : (Path.IsPathRooted(directory) ? directory : Path.GetFullPath(Path.Combine(rootDir, directory)));

            if (!Directory.Exists(targetDir))
                return $"Directory not found: {targetDir}";

            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.GetFiles(targetDir, pattern, searchOption)
                .Select(f => recursive ? GetRelativePath(rootDir, f) : (Path.GetFileName(f) ?? string.Empty))
                .Where(f => !string.IsNullOrEmpty(f))
                .OrderBy(f => f);

            var fileList = files.ToList();
            _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → {fileList.Count} files found");

            return string.Join("\n", fileList);
        }
    }

    private static string GetRelativePath(string root, string fullPath)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(fullPath)) return fullPath;
        try
        {
            var rootUri = new System.Uri(root.EndsWith("\\") ? root : root + "\\");
            var fileUri = new System.Uri(fullPath);
            return System.Uri.UnescapeDataString(rootUri.MakeRelativeUri(fileUri).ToString().Replace('/', Path.DirectorySeparatorChar));
        }
        catch { return fullPath; }
    }
}


