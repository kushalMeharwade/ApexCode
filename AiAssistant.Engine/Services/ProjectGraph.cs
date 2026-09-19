using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace AiAssistant.Engine.Services;

/// <summary>
/// Lightweight project graph for boundary checking.
/// Parses .sln and .csproj XML directly (no MSBuild evaluation).
/// Provides lazy Roslyn Compilation loading per-project on demand.
/// </summary>
public class ProjectGraph
{
    private readonly Dictionary<string, ProjectInfo> _projectsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProjectInfo> _projectsByFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _solutionPath;

    public ProjectGraph(string? solutionPath = null)
    {
        _solutionPath = solutionPath;
    }

    /// <summary>
    /// Loads project structure from solution file (fast, XML parsing only).
    /// </summary>
    public async Task LoadFromSolutionAsync(string solutionPath)
    {
        if (!File.Exists(solutionPath))
            throw new FileNotFoundException($"Solution file not found: {solutionPath}");

        var solutionDir = Path.GetDirectoryName(solutionPath) ?? "";
        var projectPaths = ParseSolutionForProjects(solutionPath, solutionDir);

        // Load each project in parallel
        var loadTasks = projectPaths.Select(path => LoadProjectAsync(path));
        await Task.WhenAll(loadTasks);
    }

    /// <summary>
    /// Loads a single project from .csproj file.
    /// </summary>
    private async Task LoadProjectAsync(string csprojPath)
    {
        if (!File.Exists(csprojPath))
            return;

        try
        {
            var doc = await Task.Run(() => XDocument.Load(csprojPath));
            var root = doc.Root;
            if (root == null) return;

            var projectDir = Path.GetDirectoryName(csprojPath) ?? "";

            // Extract project references
            var projectReferences = root.Descendants()
                .Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => e.Attribute("Include")?.Value)
                .Where(include => !string.IsNullOrEmpty(include))
                .Select(include => Path.GetFullPath(Path.Combine(projectDir, include!)))
                .ToList();

            // Extract root namespace
            var rootNamespace = root.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "RootNamespace")
                ?.Value ?? Path.GetFileNameWithoutExtension(csprojPath);

            // Extract output type
            var outputTypeStr = root.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "OutputType")
                ?.Value ?? "Library";
            var outputType = ParseOutputType(outputTypeStr);

            // Find all source files in project directory
            var sourceFiles = Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var projectInfo = new ProjectInfo
            {
                ProjectPath = csprojPath,
                RootNamespace = rootNamespace,
                OutputType = outputType,
                ProjectReferences = projectReferences,
                Files = sourceFiles,
                CompilationLoader = null // Lazy loaded on first access
            };

            _projectsByPath[csprojPath] = projectInfo;

            // Index files for quick lookup
            foreach (var file in sourceFiles)
            {
                _projectsByFile[file] = projectInfo;
            }
        }
        catch (Exception ex)
        {
            // Log error but don't fail entire graph load
            System.Diagnostics.Debug.WriteLine($"[ProjectGraph] Failed to load {csprojPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the project containing a given file.
    /// </summary>
    public ProjectInfo? GetProject(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return null;

        var normalized = Path.GetFullPath(filePath);
        return _projectsByFile.TryGetValue(normalized, out var project) ? project : null;
    }

    /// <summary>
    /// Checks if source project can reference target project.
    /// </summary>
    public bool CanReference(ProjectInfo? source, ProjectInfo? target)
    {
        if (source == null || target == null)
            return false;

        // Same project can always reference itself
        if (string.Equals(source.ProjectPath, target.ProjectPath, StringComparison.OrdinalIgnoreCase))
            return true;

        // Check if source has a ProjectReference to target
        return source.ProjectReferences.Any(refPath => 
            string.Equals(refPath, target.ProjectPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets all projects referenced by the given project.
    /// </summary>
    public List<ProjectInfo> GetReferencedProjects(ProjectInfo project)
    {
        return project.ProjectReferences
            .Select(refPath => _projectsByPath.TryGetValue(refPath, out var proj) ? proj : null)
            .Where(proj => proj != null)
            .ToList()!;
    }

    /// <summary>
    /// Lazily loads Roslyn Compilation for a project (expensive, only when needed).
    /// </summary>
    public async Task<Compilation?> GetOrLoadCompilationAsync(ProjectInfo project)
    {
        if (project.CompilationLoader != null)
            return await project.CompilationLoader;

        // Start lazy loading
        project.CompilationLoader = LoadCompilationAsync(project.ProjectPath);
        return await project.CompilationLoader;
    }

    // --- Private Helpers ---

    private static List<string> ParseSolutionForProjects(string solutionPath, string solutionDir)
    {
        var projects = new List<string>();

        try
        {
            var lines = File.ReadAllLines(solutionPath);
            foreach (var line in lines)
            {
                // Parse lines like: Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "ProjectName", "Path\To\Project.csproj", "{GUID}"
                if (line.StartsWith("Project(", StringComparison.Ordinal))
                {
                    var parts = line.Split(new[] { '"' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4)
                    {
                        var relativePath = parts[3];
                        if (relativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                        {
                            var fullPath = Path.GetFullPath(Path.Combine(solutionDir, relativePath));
                            projects.Add(fullPath);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ProjectGraph] Failed to parse solution: {ex.Message}");
        }

        return projects;
    }

    private static ProjectOutputType ParseOutputType(string outputType)
    {
        return outputType.ToLowerInvariant() switch
        {
            "exe" => ProjectOutputType.Exe,
            "winexe" => ProjectOutputType.WinExe,
            "library" => ProjectOutputType.Library,
            _ => ProjectOutputType.Library
        };
    }

    private async Task<Compilation?> LoadCompilationAsync(string csprojPath)
    {
        try
        {
            // This is the expensive operation - only called when we actually need symbol resolution
            // for a type in this project. Uses AdhocWorkspace for performance.
            
            if (!_projectsByPath.TryGetValue(csprojPath, out var project))
                return null;

            var workspace = new AdhocWorkspace();
            var projectInfo = Microsoft.CodeAnalysis.ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Create(),
                Path.GetFileNameWithoutExtension(project.ProjectPath),
                Path.GetFileNameWithoutExtension(project.ProjectPath),
                LanguageNames.CSharp);

            var roslynProject = workspace.AddProject(projectInfo);

            // Add source files
            foreach (var filePath in project.Files)
            {
                if (File.Exists(filePath))
                {
                    try
                    {
                        var sourceText = await Task.Run(() => SourceText.From(File.ReadAllText(filePath)));
                        var documentId = DocumentId.CreateNewId(roslynProject.Id);
                        roslynProject = roslynProject.AddDocument(Path.GetFileName(filePath), sourceText, filePath: filePath).Project;
                    }
                    catch
                    {
                        // Skip files that can't be read
                        continue;
                    }
                }
            }

            // Add basic framework references
            var references = new List<MetadataReference>
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location)
            };

            // Try to load referenced assemblies from bin folder (if available)
            var projectDir = Path.GetDirectoryName(project.ProjectPath);
            if (!string.IsNullOrEmpty(projectDir))
            {
                var binDebug = Path.Combine(projectDir, "bin", "Debug");
                var binRelease = Path.Combine(projectDir, "bin", "Release");
                
                foreach (var binDir in new[] { binDebug, binRelease })
                {
                    if (Directory.Exists(binDir))
                    {
                        try
                        {
                            var dlls = Directory.GetFiles(binDir, "*.dll", SearchOption.TopDirectoryOnly);
                            foreach (var dll in dlls.Take(50)) // Limit to prevent excessive loading
                            {
                                try
                                {
                                    references.Add(MetadataReference.CreateFromFile(dll));
                                }
                                catch { /* Skip invalid DLLs */ }
                            }
                            break; // Use first available bin folder
                        }
                        catch { /* Skip if can't enumerate */ }
                    }
                }
            }

            roslynProject = roslynProject.AddMetadataReferences(references);

            // Get compilation
            var compilation = await roslynProject.GetCompilationAsync();
            
            System.Diagnostics.Debug.WriteLine($"[ProjectGraph] Loaded compilation for {Path.GetFileName(csprojPath)} with {project.Files.Count} files");
            
            return compilation;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ProjectGraph] Failed to load compilation for {csprojPath}: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// Information about a single project in the solution.
/// </summary>
public class ProjectInfo
{
    public string ProjectPath { get; init; } = "";
    public string RootNamespace { get; init; } = "";
    public ProjectOutputType OutputType { get; init; }
    public List<string> ProjectReferences { get; init; } = new();
    public HashSet<string> Files { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    
    /// <summary>
    /// Lazy loader for Roslyn Compilation (expensive, loaded on-demand).
    /// </summary>
    public Task<Compilation?>? CompilationLoader { get; set; }
}

public enum ProjectOutputType
{
    Library,
    Exe,
    WinExe
}
