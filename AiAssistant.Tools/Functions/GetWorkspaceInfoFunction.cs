using System.ComponentModel;
using System.Text;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Collections.Generic;
using AiAssistant.Tools.Services;
using AiAssistant.Core.Services;
using Microsoft.Extensions.AI;

namespace AiAssistant.Tools.Functions;

public class GetWorkspaceInfoFunction : IToolProvider
{
    private readonly IVisualStudioEnvironmentService _vsEnvService;
    private readonly IOutputLogger? _outputLogger;

    public GetWorkspaceInfoFunction(IVisualStudioEnvironmentService vsEnvService, IOutputLogger? outputLogger = null)
    {
        _vsEnvService = vsEnvService;
        _outputLogger = outputLogger;
    }

    [Description("Gets information about the current workspace solution and projects loaded in Visual Studio.")]
    public AIFunction CreateFunction() => new GetWorkspaceInfoCustomFunction(_vsEnvService, _outputLogger);

    private class GetWorkspaceInfoCustomFunction : CustomAIFunction
    {
        private readonly IVisualStudioEnvironmentService _vsEnvService;
        private readonly IOutputLogger? _outputLogger;

        public GetWorkspaceInfoCustomFunction(IVisualStudioEnvironmentService vsEnvService, IOutputLogger? outputLogger)
            : base("get_workspace_context",
                   "Returns workspace root, project/folder entries, discovered project files and file-type counts for solutions and Open Folder workspaces.",
                   @"{
                        ""type"": ""object"",
                        ""properties"": {
                            ""includeFiles"": { ""type"": ""boolean"", ""description"": ""If true, includes up to 100 workspace file paths, excluding build, dependency and metadata directories."" }
                        }
                   }")
        {
            _vsEnvService = vsEnvService;
            _outputLogger = outputLogger;
        }

        protected override async Task<object> InvokeCoreImplAsync(IReadOnlyDictionary<string, object> arguments, System.Threading.CancellationToken cancellationToken)
        {
            bool includeFiles = false;
            if (arguments.TryGetValue("includeFiles", out var incObj))
            {
                if (incObj is System.Text.Json.JsonElement element && (element.ValueKind == System.Text.Json.JsonValueKind.True || element.ValueKind == System.Text.Json.JsonValueKind.False))
                    includeFiles = element.GetBoolean();
                else if (incObj is bool b)
                    includeFiles = b;
                else if (incObj is IConvertible conv)
                    includeFiles = conv.ToBoolean(null);
            }

            _outputLogger?.Log(LogCategory.Tool, $"► {Name}(includeFiles={includeFiles})");

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("## Visual Studio Workspace Info");
                sb.AppendLine();

                // Get solution from VS
                var workspacePath = await _vsEnvService.GetWorkspaceRootAsync(cancellationToken);
                var directory = workspacePath;
                if (!string.IsNullOrEmpty(workspacePath))
                {
                    sb.AppendLine($"**Workspace Directory:** {workspacePath}");
                    directory = workspacePath;
                }
                else
                {
                    sb.AppendLine("**Workspace:** No solution is currently loaded and no folder workspace is open in Visual Studio.");
                    return sb.ToString();
                }

                // Get projects from VS
                var allProjects = (await _vsEnvService.GetLoadedProjectsAsync()).ToList();

                sb.AppendLine($"**Projects:** {allProjects.Count}");
                foreach (var proj in allProjects.Take(20))
                {
                    var name = Path.GetFileName(proj);
                    var relativePath = GetRelativePath(directory, proj);
                    sb.AppendLine($"  - {name} ({relativePath}){(Directory.Exists(proj) ? " [folder entry]" : "")}");
                }
                if (allProjects.Count > 20)
                    sb.AppendLine($"  ... and {allProjects.Count - 20} more projects");

                var scan = await Task.Run(() => ScanWorkspace(directory!, cancellationToken), cancellationToken).ConfigureAwait(false);
                var files = scan.Files;
                var csFiles = files.Where(f => string.Equals(Path.GetExtension(f), ".cs", System.StringComparison.OrdinalIgnoreCase)).ToList();
                sb.AppendLine($"**C# Files:** {csFiles.Count}");
                sb.AppendLine($"**Workspace Files:** {files.Count}");
                sb.AppendLine("### File Types");
                foreach (var group in files.GroupBy(f => Path.GetExtension(f).ToLowerInvariant()).OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
                    sb.AppendLine($"  - {(group.Key.Length == 0 ? "(no extension)" : group.Key)}: {group.Count()}");

                var manifests = files.Where(f => Path.GetExtension(f).EndsWith("proj", System.StringComparison.OrdinalIgnoreCase)
                    || new[] { ".sln", ".slnx" }.Contains(Path.GetExtension(f), System.StringComparer.OrdinalIgnoreCase)
                    || new[] { "package.json", "CMakeLists.txt", "pyproject.toml", "Cargo.toml", "go.mod" }.Contains(Path.GetFileName(f), System.StringComparer.OrdinalIgnoreCase)).ToList();
                sb.AppendLine($"### Project / Solution Files on Disk ({manifests.Count})");
                foreach (var file in manifests.Take(50))
                    sb.AppendLine($"  - {GetRelativePath(directory!, file)}");
                if (manifests.Count > 50) sb.AppendLine($"  ... and {manifests.Count - 50} more project/solution files");
                sb.AppendLine("Files on disk are not necessarily loaded projects. Build, dependency and metadata directories and links are excluded.");
                if (scan.Skipped > 0) sb.AppendLine($"**Partial scan:** {scan.Skipped} inaccessible directories skipped.");

                if (includeFiles && files.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("### Source Files / Workspace Files");
                    foreach (var file in files.Take(100))
                    {
                        var relativeFilePath = GetRelativePath(directory, file);
                        sb.AppendLine($"  - {relativeFilePath}");
                    }
                    if (files.Count > 100)
                        sb.AppendLine($"  ... and {files.Count - 100} more files");
                }

                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → {allProjects.Count} projects, {csFiles.Count} files");
                return sb.ToString();
            }
            catch (System.OperationCanceledException) { throw; }
            catch (System.Exception ex)
            {
                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → Error: {ex.Message}");
                return $"Error getting workspace info: {ex.Message}";
            }
        }
    }

    private static readonly HashSet<string> ExcludedDirectories = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", "node_modules", "packages", ".nuget",
        "dist", "build", "out", "__pycache__", ".venv", "venv"
    };

    private static (List<string> Files, int Skipped) ScanWorkspace(string root, System.Threading.CancellationToken ct)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        int skipped = 0;
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    ct.ThrowIfCancellationRequested();
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        if (!ExcludedDirectories.Contains(entry.Name)) pending.Push(entry.FullName);
                    }
                    else files.Add(entry.FullName);
                }
            }
            catch (System.UnauthorizedAccessException) { skipped++; }
            catch (IOException) { skipped++; }
        }
        files.Sort(System.StringComparer.OrdinalIgnoreCase);
        return (files, skipped);
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


