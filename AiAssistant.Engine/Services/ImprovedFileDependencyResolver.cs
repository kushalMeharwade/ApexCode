using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Services;

namespace AiAssistant.Engine.Services;

/// <summary>
/// Improved file dependency resolver with proper cycle detection, token budgets,
/// and project boundary enforcement.
/// </summary>
public class ImprovedFileDependencyResolver : IFileDependencyResolver
{
    private readonly IVisualStudioEnvironmentService _vsEnvService;
    private readonly ISettingsService _settingsService;
    private ProjectGraph? _projectGraph;
    private CSharpDependencyResolver? _csharpResolver;
    private TypeScriptDependencyResolver? _typescriptResolver;
    private PythonDependencyResolver? _pythonResolver;
    private readonly CompanionFileResolver _companionFileResolver;
    private readonly TokenEstimator _tokenEstimator;
    private readonly ResolutionPolicy _policy;

    public ImprovedFileDependencyResolver(
        IVisualStudioEnvironmentService vsEnvService,
        ISettingsService settingsService,
        ResolutionPolicy? policy = null)
    {
        _vsEnvService = vsEnvService;
        _settingsService = settingsService;
        _policy = policy ?? new ResolutionPolicy();
        _tokenEstimator = new TokenEstimator(_policy.TokenEstimationStrategy);
        _companionFileResolver = new CompanionFileResolver();
    }

    /// <summary>
    /// Resolves dependencies for a set of explicitly mentioned files.
    /// </summary>
    public async Task<IEnumerable<string>> ResolveDependenciesAsync(IEnumerable<string> sourceFiles)
    {
        var cancellationToken = CancellationToken.None;
        var graph = new DependencyGraph();
        var sourceFilesList = sourceFiles.ToList();

        // Load project graph if not already loaded
        await EnsureProjectGraphLoadedAsync(cancellationToken);

        if (_projectGraph == null || _csharpResolver == null)
        {
            // Fallback: return source files only
            return sourceFilesList;
        }

        // Add explicit files to graph first
        foreach (var file in sourceFilesList)
        {
            if (graph.IsVisited(file))
                continue;

            graph.MarkVisited(file);
            var estimatedTokens = await _tokenEstimator.EstimateAsync(file);
            graph.AddInferredDependency(file, "Explicit user mention", 1.0f, 0, estimatedTokens, 0);
        }

        // Resolve dependencies recursively
        foreach (var file in sourceFilesList)
        {
            await ResolveFileDependenciesAsync(file, graph, 0, cancellationToken);
        }

        // Get filtered results based on policy
        var resolvedNodes = graph.GetResolvedFiles(_policy);

        // Return file paths
        return resolvedNodes.Select(n => n.FilePath).Distinct();
    }

    /// <summary>
    /// Recursively resolves dependencies for a single file.
    /// </summary>
    private async Task ResolveFileDependenciesAsync(
        string filePath,
        DependencyGraph graph,
        int currentDepth,
        CancellationToken cancellationToken)
    {
        // Check depth limit
        if (currentDepth >= _policy.MaxDepth)
            return;

        // Check if file is C#
        if (filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            var dependencies = await _csharpResolver!.ResolveDependenciesAsync(filePath, cancellationToken);

            foreach (var dep in dependencies)
            {
                // Cycle detection
                if (graph.IsVisited(dep.FilePath))
                    continue;

                graph.MarkVisited(dep.FilePath);

                // Estimate tokens
                var estimatedTokens = await _tokenEstimator.EstimateAsync(dep.FilePath);

                // Add to graph
                if (dep.Tier == 1)
                {
                    graph.AddExplicitDependency(filePath, dep.FilePath, dep.Reason, estimatedTokens);
                }
                else
                {
                    graph.AddInferredDependency(dep.FilePath, dep.Reason, dep.Confidence, dep.Tier, estimatedTokens, currentDepth + 1);
                }

                // Recursive resolution
                if (currentDepth + 1 < _policy.MaxDepth)
                {
                    await ResolveFileDependenciesAsync(dep.FilePath, graph, currentDepth + 1, cancellationToken);
                }
            }
        }
        // TypeScript/JavaScript files
        else if (filePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
                 filePath.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ||
                 filePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                 filePath.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase))
        {
            await EnsureTypeScriptResolverLoadedAsync();
            
            if (_typescriptResolver != null)
            {
                var dependencies = await _typescriptResolver.ResolveDependenciesAsync(filePath, cancellationToken);
                await AddDependenciesToGraphAsync(filePath, dependencies, graph, currentDepth, cancellationToken);
            }

            // Also resolve companion files (HTML, CSS)
            var companions = _companionFileResolver.ResolveDependencies(filePath);
            await AddDependenciesToGraphAsync(filePath, companions, graph, currentDepth, cancellationToken);
        }
        // Python files
        else if (filePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
        {
            await EnsurePythonResolverLoadedAsync();

            if (_pythonResolver != null)
            {
                var dependencies = await _pythonResolver.ResolveDependenciesAsync(filePath, cancellationToken);
                await AddDependenciesToGraphAsync(filePath, dependencies, graph, currentDepth, cancellationToken);
            }
        }
        else
        {
            // Other files: use companion file resolution only
            var dependencies = _companionFileResolver.ResolveDependencies(filePath);
            await AddDependenciesToGraphAsync(filePath, dependencies, graph, currentDepth, cancellationToken);
        }
    }

    /// <summary>
    /// Helper to add dependencies to graph with cycle detection and recursion.
    /// </summary>
    private async Task AddDependenciesToGraphAsync(
        string sourceFile,
        List<ResolvedDependency> dependencies,
        DependencyGraph graph,
        int currentDepth,
        CancellationToken cancellationToken)
    {
        foreach (var dep in dependencies)
        {
            // Cycle detection
            if (graph.IsVisited(dep.FilePath))
                continue;

            graph.MarkVisited(dep.FilePath);

            // Estimate tokens
            var estimatedTokens = await _tokenEstimator.EstimateAsync(dep.FilePath);

            // Add to graph
            if (dep.Tier == 1)
            {
                graph.AddExplicitDependency(sourceFile, dep.FilePath, dep.Reason, estimatedTokens);
            }
            else
            {
                graph.AddInferredDependency(dep.FilePath, dep.Reason, dep.Confidence, dep.Tier, estimatedTokens, currentDepth + 1);
            }

            // Recursive resolution
            if (currentDepth + 1 < _policy.MaxDepth)
            {
                await ResolveFileDependenciesAsync(dep.FilePath, graph, currentDepth + 1, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Ensures TypeScript resolver is loaded (lazy initialization).
    /// </summary>
    private async Task EnsureTypeScriptResolverLoadedAsync()
    {
        if (_typescriptResolver != null)
            return;

        try
        {
            var workspaceRoot = await _vsEnvService.GetWorkspaceRootAsync();
            if (string.IsNullOrEmpty(workspaceRoot))
                return;

            // Load tsconfig path mappings
            var pathMappings = await TypeScriptDependencyResolver.LoadPathMappingsFromTsConfigAsync(workspaceRoot!);
            _typescriptResolver = new TypeScriptDependencyResolver(workspaceRoot!, pathMappings);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ImprovedResolver] Failed to load TypeScript resolver: {ex.Message}");
        }
    }

    /// <summary>
    /// Ensures Python resolver is loaded (lazy initialization).
    /// </summary>
    private async Task EnsurePythonResolverLoadedAsync()
    {
        if (_pythonResolver != null)
            return;

        try
        {
            var workspaceRoot = await _vsEnvService.GetWorkspaceRootAsync();
            if (string.IsNullOrEmpty(workspaceRoot))
                return;

            _pythonResolver = new PythonDependencyResolver(workspaceRoot!);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ImprovedResolver] Failed to load Python resolver: {ex.Message}");
        }
    }

    /// <summary>
    /// Ensures project graph is loaded (lazy initialization).
    /// </summary>
    private async Task EnsureProjectGraphLoadedAsync(CancellationToken cancellationToken)
    {
        if (_projectGraph != null)
            return;

        try
        {
            var workspaceRoot = await _vsEnvService.GetWorkspaceRootAsync();
            if (string.IsNullOrEmpty(workspaceRoot))
                return;

            // Look for .sln file
            var solutionFiles = System.IO.Directory.GetFiles(workspaceRoot, "*.sln", System.IO.SearchOption.TopDirectoryOnly);
            if (solutionFiles.Length == 0)
                return;

            var solutionPath = solutionFiles[0];

            _projectGraph = new ProjectGraph(solutionPath);
            await _projectGraph.LoadFromSolutionAsync(solutionPath);

            _csharpResolver = new CSharpDependencyResolver(_projectGraph);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ImprovedResolver] Failed to load project graph: {ex.Message}");
            // Continue without project graph - will use fallback resolution
        }
    }
}
