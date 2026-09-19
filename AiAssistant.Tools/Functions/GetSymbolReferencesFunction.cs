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

public class GetSymbolReferencesFunction : IToolProvider
{
    private readonly IVisualStudioEnvironmentService _vsEnvService;
    private readonly IOutputLogger? _outputLogger;

    public GetSymbolReferencesFunction(IVisualStudioEnvironmentService vsEnvService, IOutputLogger? outputLogger = null)
    {
        _vsEnvService = vsEnvService;
        _outputLogger = outputLogger;
    }

    [Description("Use this when you only know the type/method name and want to find all references across the solution. For precise location-based lookups, use find_references_at_cursor instead.")]
    public AIFunction CreateFunction() => new GetSymbolReferencesCustomFunction(_vsEnvService, _outputLogger);

    private class GetSymbolReferencesCustomFunction : CustomAIFunction
    {
        private readonly IVisualStudioEnvironmentService _vsEnvService;
        private readonly IOutputLogger? _outputLogger;

        public GetSymbolReferencesCustomFunction(IVisualStudioEnvironmentService vsEnvService, IOutputLogger? outputLogger)
            : base("find_references_by_symbol_name",
                   "Use this when you only know the type/method name and want to find all references across the solution. For precise location-based lookups, use find_references_at_cursor instead.",
                   @"{
                        ""type"": ""object"",
                        ""properties"": {
                            ""symbolName"": { ""type"": ""string"", ""description"": ""The fully qualified name of the symbol, or a unique name."" }
                        },
                        ""required"": [""symbolName""]
                   }")
        {
            _vsEnvService = vsEnvService;
            _outputLogger = outputLogger;
        }

        protected override async Task<object> InvokeCoreImplAsync(IReadOnlyDictionary<string, object> arguments, System.Threading.CancellationToken cancellationToken)
        {
            if (!arguments.TryGetValue("symbolName", out var symObj) || symObj?.ToString() is not string symbolName)
                return "Error: symbolName argument is missing.";

            _outputLogger?.Log(LogCategory.Tool, $"► {Name}({symbolName})");

            try
            {
                var workspace = await _vsEnvService.GetWorkspaceAsync();
                if (workspace == null)
                    return "No active Roslyn workspace found. Make sure a solution is open.";

                var solution = workspace.CurrentSolution;
                var symbols = new List<ISymbol>();

                foreach (var project in solution.Projects)
                {
                    var compilation = await project.GetCompilationAsync();
                    if (compilation == null) continue;

                    // This is a naive lookup, it only finds types for now
                    var typeSymbol = compilation.GetTypeByMetadataName(symbolName);
                    if (typeSymbol != null)
                    {
                        symbols.Add(typeSymbol);
                    }
                    else
                    {
                        // Fallback: search for methods or other symbols by name
                        var allTypes = compilation.GlobalNamespace.GetNamespaceMembers()
                            .SelectMany(GetTypes)
                            .ToList();

                        foreach (var type in allTypes)
                        {
                            var members = type.GetMembers(symbolName);
                            symbols.AddRange(members);
                        }
                    }
                }

                symbols = symbols.Distinct(SymbolEqualityComparer.Default).ToList();

                if (symbols.Count == 0)
                {
                    return $"No symbol found matching '{symbolName}'.";
                }

                var targetSymbol = symbols.First(); // use first match
                var references = await SymbolFinder.FindReferencesAsync(targetSymbol, solution);

                var results = new List<string> { $"References for {targetSymbol.ToDisplayString()}:" };

                foreach (var referencedSymbol in references)
                {
                    foreach (var location in referencedSymbol.Locations)
                    {
                        var lineSpan = location.Location.GetLineSpan();
                        var relativePath = GetRelativePath(solution.FilePath, location.Location.SourceTree?.FilePath);
                        results.Add($"{relativePath}:{lineSpan.StartLinePosition.Line + 1}");
                    }
                }

                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → {results.Count - 1} references found");
                return string.Join("\n", results.Distinct());
            }
            catch (Exception ex)
            {
                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → Error: {ex.Message}");
                return $"Error finding references: {ex.Message}";
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> GetTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
            yield return type;

        foreach (var childNs in ns.GetNamespaceMembers())
        foreach (var type in GetTypes(childNs))
            yield return type;
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


