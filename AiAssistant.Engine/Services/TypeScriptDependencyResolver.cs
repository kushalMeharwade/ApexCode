using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AiAssistant.Engine.Services;

/// <summary>
/// Resolves dependencies for TypeScript/JavaScript files using import/require statement analysis.
/// Handles: import, require, dynamic import, re-exports.
/// </summary>
public class TypeScriptDependencyResolver
{
    private readonly string _workspaceRoot;
    private readonly Dictionary<string, string> _pathMappings; // tsconfig path aliases

    // Patterns to match import/require statements
    private static readonly Regex ImportRegex = new Regex(
        @"^\s*import\s+(?:(?:[\w*\s{},]*)\s+from\s+)?['""]([^'""]+)['""]",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex RequireRegex = new Regex(
        @"require\(['""]([^'""]+)['""]\)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex DynamicImportRegex = new Regex(
        @"import\(['""]([^'""]+)['""]\)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex ExportFromRegex = new Regex(
        @"export\s+(?:[\w*\s{},]*)\s+from\s+['""]([^'""]+)['""]",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public TypeScriptDependencyResolver(string workspaceRoot, Dictionary<string, string>? pathMappings = null)
    {
        _workspaceRoot = workspaceRoot;
        _pathMappings = pathMappings ?? new Dictionary<string, string>();
    }

    /// <summary>
    /// Resolves dependencies for a TypeScript/JavaScript file.
    /// </summary>
    public async Task<List<ResolvedDependency>> ResolveDependenciesAsync(
        string sourceFilePath,
        CancellationToken cancellationToken = default)
    {
        var dependencies = new List<ResolvedDependency>();

        if (!File.Exists(sourceFilePath))
            return dependencies;

        var extension = Path.GetExtension(sourceFilePath).ToLowerInvariant();
        if (extension != ".ts" && extension != ".tsx" && extension != ".js" && extension != ".jsx")
            return dependencies;

        try
        {
            var content = await Task.Run(() => File.ReadAllText(sourceFilePath), cancellationToken);
            var importPaths = ExtractImportPaths(content);

            var sourceDirectory = Path.GetDirectoryName(sourceFilePath) ?? "";

            foreach (var importPath in importPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Skip node_modules and external packages
                if (IsExternalPackage(importPath))
                    continue;

                // Resolve to absolute path
                var resolvedPath = await ResolveImportPathAsync(importPath, sourceDirectory, sourceFilePath);
                if (resolvedPath != null && File.Exists(resolvedPath))
                {
                    dependencies.Add(new ResolvedDependency
                    {
                        FilePath = resolvedPath,
                        Confidence = 1.0f,
                        Reason = $"Import statement: {importPath}",
                        Tier = 1
                    });
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TSResolver] Error resolving {sourceFilePath}: {ex.Message}");
        }

        return dependencies;
    }

    /// <summary>
    /// Extracts all import/require paths from source code.
    /// </summary>
    private HashSet<string> ExtractImportPaths(string content)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ES6 imports: import { foo } from './bar'
        foreach (Match match in ImportRegex.Matches(content))
        {
            if (match.Groups.Count > 1)
                paths.Add(match.Groups[1].Value);
        }

        // CommonJS require: require('./bar')
        foreach (Match match in RequireRegex.Matches(content))
        {
            if (match.Groups.Count > 1)
                paths.Add(match.Groups[1].Value);
        }

        // Dynamic imports: import('./bar')
        foreach (Match match in DynamicImportRegex.Matches(content))
        {
            if (match.Groups.Count > 1)
                paths.Add(match.Groups[1].Value);
        }

        // Re-exports: export { foo } from './bar'
        foreach (Match match in ExportFromRegex.Matches(content))
        {
            if (match.Groups.Count > 1)
                paths.Add(match.Groups[1].Value);
        }

        return paths;
    }

    /// <summary>
    /// Checks if an import path refers to an external package (not a local file).
    /// </summary>
    private bool IsExternalPackage(string importPath)
    {
        // External packages don't start with ./ or ../ or /
        if (importPath.StartsWith("./") || importPath.StartsWith("../") || importPath.StartsWith("/"))
            return false;

        // Also not external if it matches a path alias
        if (_pathMappings.Count > 0 && _pathMappings.Keys.Any(alias => importPath.StartsWith(alias)))
            return false;

        return true;
    }

    /// <summary>
    /// Resolves an import path to an absolute file path.
    /// Handles: relative paths, path aliases (tsconfig), index files, extension resolution.
    /// </summary>
    private async Task<string?> ResolveImportPathAsync(string importPath, string sourceDirectory, string sourceFilePath)
    {
        // Handle path aliases (e.g., @app/services -> src/app/services)
        var resolvedImportPath = ResolvePathAlias(importPath);

        // Relative path resolution
        string basePath;
        if (resolvedImportPath.StartsWith("./") || resolvedImportPath.StartsWith("../"))
        {
            basePath = Path.GetFullPath(Path.Combine(sourceDirectory, resolvedImportPath));
        }
        else if (resolvedImportPath.StartsWith("/"))
        {
            basePath = Path.GetFullPath(Path.Combine(_workspaceRoot, resolvedImportPath.TrimStart('/')));
        }
        else
        {
            // Might be a path alias resolved to relative path
            basePath = Path.GetFullPath(Path.Combine(_workspaceRoot, resolvedImportPath));
        }

        // Try different extensions and index files
        var candidates = new List<string>
        {
            basePath,                           // Exact path
            basePath + ".ts",                   // .ts extension
            basePath + ".tsx",                  // .tsx extension
            basePath + ".js",                   // .js extension
            basePath + ".jsx",                  // .jsx extension
            Path.Combine(basePath, "index.ts"), // index.ts
            Path.Combine(basePath, "index.tsx"),// index.tsx
            Path.Combine(basePath, "index.js"), // index.js
            Path.Combine(basePath, "index.jsx") // index.jsx
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        await Task.CompletedTask; // For async compatibility
        return null;
    }

    /// <summary>
    /// Resolves TypeScript path aliases (from tsconfig.json paths).
    /// Example: @app/services -> src/app/services
    /// </summary>
    private string ResolvePathAlias(string importPath)
    {
        if (_pathMappings.Count == 0)
            return importPath;

        foreach (var mapping in _pathMappings)
        {
            var alias = mapping.Key.TrimEnd('*');
            if (importPath.StartsWith(alias))
            {
                var targetPath = mapping.Value.TrimEnd('*');
                return importPath.Replace(alias, targetPath);
            }
        }

        return importPath;
    }

    /// <summary>
    /// Loads path mappings from tsconfig.json if available.
    /// </summary>
    public static async Task<Dictionary<string, string>> LoadPathMappingsFromTsConfigAsync(string workspaceRoot)
    {
        var mappings = new Dictionary<string, string>();

        try
        {
            var tsconfigPath = Path.Combine(workspaceRoot, "tsconfig.json");
            if (!File.Exists(tsconfigPath))
                return mappings;

            var content = await Task.Run(() => File.ReadAllText(tsconfigPath));
            
            // Simple JSON parsing for paths (avoiding full JSON library dependency)
            // Look for: "paths": { "@app/*": ["src/app/*"] }
            var pathsMatch = Regex.Match(content, @"""paths""\s*:\s*\{([^}]+)\}", RegexOptions.Singleline);
            if (pathsMatch.Success)
            {
                var pathsContent = pathsMatch.Groups[1].Value;
                var pathEntries = Regex.Matches(pathsContent, @"""([^""]+)""\s*:\s*\[\s*""([^""]+)""\s*\]");

                foreach (Match entry in pathEntries)
                {
                    if (entry.Groups.Count > 2)
                    {
                        var alias = entry.Groups[1].Value;
                        var target = entry.Groups[2].Value;
                        mappings[alias] = target;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TSResolver] Failed to load tsconfig.json: {ex.Message}");
        }

        return mappings;
    }
}
