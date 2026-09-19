using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AiAssistant.Engine.Services;

/// <summary>
/// Resolves dependencies for Python files using import statement analysis.
/// </summary>
public class PythonDependencyResolver
{
    private readonly string _workspaceRoot;

    private static readonly Regex ImportRegex = new Regex(
        @"^\s*(?:from\s+([\w.]+)\s+)?import\s+([\w,\s*]+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public PythonDependencyResolver(string workspaceRoot)
    {
        _workspaceRoot = workspaceRoot;
    }

    public async Task<List<ResolvedDependency>> ResolveDependenciesAsync(
        string sourceFilePath,
        CancellationToken cancellationToken = default)
    {
        var dependencies = new List<ResolvedDependency>();

        if (!File.Exists(sourceFilePath) || !sourceFilePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            return dependencies;

        try
        {
            var content = await Task.Run(() => File.ReadAllText(sourceFilePath), cancellationToken);
            var imports = ExtractImports(content);

            var sourceDirectory = Path.GetDirectoryName(sourceFilePath) ?? "";

            foreach (var import in imports)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Skip standard library and external packages (heuristic)
                if (IsStandardLibrary(import))
                    continue;

                var resolvedPath = ResolveImportPath(import, sourceDirectory);
                if (resolvedPath != null && File.Exists(resolvedPath))
                {
                    dependencies.Add(new ResolvedDependency
                    {
                        FilePath = resolvedPath,
                        Confidence = 0.9f,
                        Reason = $"Python import: {import}",
                        Tier = 1
                    });
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PyResolver] Error resolving {sourceFilePath}: {ex.Message}");
        }

        return dependencies;
    }

    private HashSet<string> ExtractImports(string content)
    {
        var imports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in ImportRegex.Matches(content))
        {
            // from X import Y -> extract X
            if (match.Groups.Count > 1 && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
            {
                imports.Add(match.Groups[1].Value.Trim());
            }
            // import X, Y, Z -> extract each
            else if (match.Groups.Count > 2)
            {
                var modules = match.Groups[2].Value.Split(',');
                foreach (var module in modules)
                {
                    var cleaned = module.Trim().Split(' ')[0]; // Handle "import X as Y"
                    if (!string.IsNullOrWhiteSpace(cleaned))
                        imports.Add(cleaned);
                }
            }
        }

        return imports;
    }

    private bool IsStandardLibrary(string moduleName)
    {
        // Common Python standard library modules (not exhaustive)
        var stdModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "os", "sys", "re", "json", "datetime", "time", "collections",
            "itertools", "functools", "pathlib", "typing", "unittest",
            "logging", "math", "random", "string", "io", "subprocess"
        };

        var rootModule = moduleName.Split('.')[0];
        return stdModules.Contains(rootModule);
    }

    private string? ResolveImportPath(string modulePath, string sourceDirectory)
    {
        // Convert module.path.name to module/path/name.py
        var relativePath = modulePath.Replace('.', Path.DirectorySeparatorChar);

        var candidates = new List<string>
        {
            Path.Combine(sourceDirectory, relativePath + ".py"),
            Path.Combine(sourceDirectory, relativePath, "__init__.py"),
            Path.Combine(_workspaceRoot, relativePath + ".py"),
            Path.Combine(_workspaceRoot, relativePath, "__init__.py")
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
