using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AiAssistant.Engine.Services;

/// <summary>
/// Resolves C# file dependencies using Roslyn SemanticModel for accurate symbol resolution.
/// Handles partial classes, project boundaries, and all C# syntax patterns.
/// </summary>
public class CSharpDependencyResolver
{
    private readonly ProjectGraph _projectGraph;

    public CSharpDependencyResolver(ProjectGraph projectGraph)
    {
        _projectGraph = projectGraph;
    }

    /// <summary>
    /// Resolves all dependencies for a C# source file using Roslyn semantic analysis.
    /// </summary>
    public async Task<List<ResolvedDependency>> ResolveDependenciesAsync(
        string sourceFilePath,
        CancellationToken cancellationToken = default)
    {
        var dependencies = new List<ResolvedDependency>();

        if (!File.Exists(sourceFilePath) || !sourceFilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return dependencies;

        try
        {
            // Get the project containing this file
            var sourceProject = _projectGraph.GetProject(sourceFilePath);
            if (sourceProject == null)
            {
                System.Diagnostics.Debug.WriteLine($"[CSharpResolver] File not in any project: {sourceFilePath}");
                return dependencies;
            }

            // Load compilation for symbol resolution (expensive, but cached)
            var compilation = await _projectGraph.GetOrLoadCompilationAsync(sourceProject);
            if (compilation == null)
            {
                // Fallback: lightweight companion file resolution only
                return ResolveCompanionFiles(sourceFilePath);
            }

            // Parse the source file
            var sourceText = await Task.Run(() => File.ReadAllText(sourceFilePath), cancellationToken);
            var syntaxTree = CSharpSyntaxTree.ParseText(sourceText, path: sourceFilePath, cancellationToken: cancellationToken);
            var root = await syntaxTree.GetRootAsync(cancellationToken);

            // Get semantic model for symbol resolution
            var semanticModel = compilation.GetSemanticModel(syntaxTree);

            // Walk all syntax nodes that can reference types
            var nodesToCheck = root.DescendantNodes().Where(node =>
                node is IdentifierNameSyntax ||       // Bare identifiers: MyType
                node is GenericNameSyntax ||          // Generic types: List<UserDto>
                node is QualifiedNameSyntax ||        // Qualified: System.String
                node is MemberAccessExpressionSyntax || // Member access: _service.GetUser()
                node is AttributeSyntax);             // Attributes: [Authorize]

            var visitedTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

            foreach (var node in nodesToCheck)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var symbolInfo = semanticModel.GetSymbolInfo(node, cancellationToken);
                
                // Handle type symbols (classes, interfaces, structs, enums)
                if (symbolInfo.Symbol is INamedTypeSymbol typeSymbol)
                {
                    if (visitedTypes.Add(typeSymbol))
                    {
                        await ResolveTypeSymbolAsync(typeSymbol, sourceProject, dependencies, cancellationToken);
                    }
                }
                // Handle method/property symbols (for extension methods, etc.)
                else if (symbolInfo.Symbol is IMethodSymbol methodSymbol)
                {
                    var declaringType = methodSymbol.ContainingType;
                    if (declaringType != null && visitedTypes.Add(declaringType))
                    {
                        await ResolveTypeSymbolAsync(declaringType, sourceProject, dependencies, cancellationToken);
                    }
                }
                else if (symbolInfo.Symbol is IPropertySymbol propertySymbol)
                {
                    var declaringType = propertySymbol.ContainingType;
                    if (declaringType != null && visitedTypes.Add(declaringType))
                    {
                        await ResolveTypeSymbolAsync(declaringType, sourceProject, dependencies, cancellationToken);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CSharpResolver] Error resolving {sourceFilePath}: {ex.Message}");
        }

        return dependencies;
    }

    /// <summary>
    /// Resolves a type symbol to its declaring file(s) with project boundary checking.
    /// </summary>
    private async Task ResolveTypeSymbolAsync(
        INamedTypeSymbol typeSymbol,
        ProjectInfo sourceProject,
        List<ResolvedDependency> dependencies,
        CancellationToken cancellationToken)
    {
        // CRITICAL: Loop over ALL locations (handles partial classes)
        var locations = typeSymbol.Locations
            .Where(loc => loc.IsInSource)
            .Select(loc => loc.SourceTree?.FilePath)
            .Where(path => !string.IsNullOrEmpty(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (filePath == null) continue;

            // Check project boundaries
            var targetProject = _projectGraph.GetProject(filePath);
            if (targetProject == null)
                continue; // File not in graph

            if (!_projectGraph.CanReference(sourceProject, targetProject))
            {
                // Cross-project boundary violation - skip
                System.Diagnostics.Debug.WriteLine(
                    $"[CSharpResolver] Skipping {filePath} - no ProjectReference from {sourceProject.ProjectPath} to {targetProject.ProjectPath}");
                continue;
            }

            // Add dependency
            dependencies.Add(new ResolvedDependency
            {
                FilePath = filePath,
                Confidence = 1.0f,
                Reason = $"Roslyn symbol resolution: {typeSymbol.ToDisplayString()}",
                Tier = 1,
                SourceProject = sourceProject.ProjectPath,
                TargetProject = targetProject.ProjectPath
            });
        }
    }

    /// <summary>
    /// Resolves companion files (fallback when compilation not available).
    /// </summary>
    private List<ResolvedDependency> ResolveCompanionFiles(string filePath)
    {
        var dependencies = new List<ResolvedDependency>();
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
            return dependencies;

        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(filePath);

        // .xaml ↔ .xaml.cs
        if (filePath.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase))
        {
            var xamlFile = filePath.Substring(0, filePath.Length - 3); // Remove ".cs"
            if (File.Exists(xamlFile))
            {
                dependencies.Add(new ResolvedDependency
                {
                    FilePath = xamlFile,
                    Confidence = 0.9f,
                    Reason = "Companion file (XAML)",
                    Tier = 2
                });
            }
        }
        else if (filePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            var codeFile = filePath + ".cs";
            if (File.Exists(codeFile))
            {
                dependencies.Add(new ResolvedDependency
                {
                    FilePath = codeFile,
                    Confidence = 0.9f,
                    Reason = "Companion file (code-behind)",
                    Tier = 2
                });
            }
        }

        // .razor ↔ .razor.cs
        else if (filePath.EndsWith(".razor.cs", StringComparison.OrdinalIgnoreCase))
        {
            var razorFile = filePath.Substring(0, filePath.Length - 3);
            if (File.Exists(razorFile))
            {
                dependencies.Add(new ResolvedDependency
                {
                    FilePath = razorFile,
                    Confidence = 0.9f,
                    Reason = "Companion file (Razor)",
                    Tier = 2
                });
            }
        }
        else if (filePath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
        {
            var codeFile = filePath + ".cs";
            if (File.Exists(codeFile))
            {
                dependencies.Add(new ResolvedDependency
                {
                    FilePath = codeFile,
                    Confidence = 0.9f,
                    Reason = "Companion file (code-behind)",
                    Tier = 2
                });
            }
        }

        // Designer files: MyForm.cs ↔ MyForm.Designer.cs
        if (filePath.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
        {
            var mainFile = Path.Combine(directory, fileNameWithoutExt.Replace(".Designer", "") + ".cs");
            if (File.Exists(mainFile) && !mainFile.Equals(filePath, StringComparison.OrdinalIgnoreCase))
            {
                dependencies.Add(new ResolvedDependency
                {
                    FilePath = mainFile,
                    Confidence = 0.9f,
                    Reason = "Companion file (main partial)",
                    Tier = 2
                });
            }
        }
        else if (File.Exists(Path.Combine(directory, fileNameWithoutExt + ".Designer.cs")))
        {
            var designerFile = Path.Combine(directory, fileNameWithoutExt + ".Designer.cs");
            dependencies.Add(new ResolvedDependency
            {
                FilePath = designerFile,
                Confidence = 0.9f,
                Reason = "Companion file (designer)",
                Tier = 2
            });
        }

        // Generated files: MyClass.cs ↔ MyClass.g.cs
        if (filePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
        {
            var mainFile = Path.Combine(directory, fileNameWithoutExt.Replace(".g", "") + ".cs");
            if (File.Exists(mainFile) && !mainFile.Equals(filePath, StringComparison.OrdinalIgnoreCase))
            {
                dependencies.Add(new ResolvedDependency
                {
                    FilePath = mainFile,
                    Confidence = 0.9f,
                    Reason = "Companion file (main for generated)",
                    Tier = 2
                });
            }
        }

        return dependencies;
    }
}

/// <summary>
/// Represents a resolved file dependency.
/// </summary>
public class ResolvedDependency
{
    public string FilePath { get; init; } = "";
    public float Confidence { get; init; }
    public string Reason { get; init; } = "";
    public int Tier { get; init; }
    public string? SourceProject { get; init; }
    public string? TargetProject { get; init; }
}
