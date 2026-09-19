using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using AiAssistant.Core.Services;
using AiAssistant.Tools.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.AI;

namespace AiAssistant.Tools.Functions;

public class GetTypeHierarchyFunction : IToolProvider
{
    private readonly IVisualStudioEnvironmentService _vsEnvService;
    private readonly IOutputLogger? _outputLogger;

    public GetTypeHierarchyFunction(IVisualStudioEnvironmentService vsEnvService, IOutputLogger? outputLogger = null)
    {
        _vsEnvService = vsEnvService;
        _outputLogger = outputLogger;
    }

    [Description("Finds all derived classes or implementations of an interface across the solution.")]
    public AIFunction CreateFunction() => new GetTypeHierarchyCustomFunction(_vsEnvService, _outputLogger);

    private class GetTypeHierarchyCustomFunction : CustomAIFunction
    {
        private readonly IVisualStudioEnvironmentService _vsEnvService;
        private readonly IOutputLogger? _outputLogger;

        public GetTypeHierarchyCustomFunction(IVisualStudioEnvironmentService vsEnvService, IOutputLogger? outputLogger)
            : base("get_type_hierarchy",
                   "Uses Roslyn to find all implementations or derived classes for a type.",
                   @"{
                        ""type"": ""object"",
                        ""properties"": {
                            ""typeName"": { ""type"": ""string"", ""description"": ""The fully qualified name of the base class or interface."" }
                        },
                        ""required"": [""typeName""]
                   }")
        {
            _vsEnvService = vsEnvService;
            _outputLogger = outputLogger;
        }

        protected override async Task<object> InvokeCoreImplAsync(IReadOnlyDictionary<string, object> arguments, System.Threading.CancellationToken cancellationToken)
        {
            if (!arguments.TryGetValue("typeName", out var tObj) || tObj?.ToString() is not string typeName)
                return "Error: typeName argument is missing.";

            _outputLogger?.Log(LogCategory.Tool, $"► {Name}({typeName})");

            try
            {
                var workspace = await _vsEnvService.GetWorkspaceAsync();
                if (workspace == null)
                    return "No active Roslyn workspace found. Make sure a solution is open.";

                var solution = workspace.CurrentSolution;
                INamedTypeSymbol? targetType = null;

                foreach (var project in solution.Projects)
                {
                    var compilation = await project.GetCompilationAsync();
                    if (compilation == null) continue;

                    targetType = compilation.GetTypeByMetadataName(typeName);
                    if (targetType != null) break;
                }

                if (targetType == null)
                {
                    return $"Type not found: '{typeName}'. Make sure to use the fully qualified metadata name (e.g., Namespace.ClassName).";
                }

                var results = new List<string> { $"Hierarchy for {targetType.ToDisplayString()}:" };

                if (targetType.TypeKind == TypeKind.Interface)
                {
                    var implementations = await SymbolFinder.FindImplementationsAsync(targetType, solution);
                    foreach (var impl in implementations)
                    {
                        var loc = impl.Locations.FirstOrDefault();
                        var relativePath = GetRelativePath(solution.FilePath, loc?.SourceTree?.FilePath);
                        var line = loc?.GetLineSpan().StartLinePosition.Line + 1 ?? 0;
                        results.Add($"- Implementation: {impl.ToDisplayString()} ({relativePath}:{line})");
                    }
                }
                else
                {
                    var derivedClasses = await SymbolFinder.FindDerivedClassesAsync(targetType, solution);
                    foreach (var derived in derivedClasses)
                    {
                        var loc = derived.Locations.FirstOrDefault();
                        var relativePath = GetRelativePath(solution.FilePath, loc?.SourceTree?.FilePath);
                        var line = loc?.GetLineSpan().StartLinePosition.Line + 1 ?? 0;
                        results.Add($"- Derived: {derived.ToDisplayString()} ({relativePath}:{line})");
                    }
                }

                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → {results.Count - 1} derived types found");
                return string.Join("\n", results);
            }
            catch (Exception ex)
            {
                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → Error: {ex.Message}");
                return $"Error finding hierarchy: {ex.Message}";
            }
        }
    }

    private static string GetRelativePath(string? solutionPath, string? fullPath)
    {
        if (string.IsNullOrEmpty(solutionPath) || string.IsNullOrEmpty(fullPath)) return fullPath ?? "";
        try
        {
            var root = System.IO.Path.GetDirectoryName(solutionPath);
            if (root == null) return fullPath;

            var rootUri = new Uri(root.EndsWith("\\") ? root : root + "\\");
            var fileUri = new Uri(fullPath);
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(fileUri).ToString().Replace('/', System.IO.Path.DirectorySeparatorChar));
        }
        catch { return fullPath; }
    }
}


