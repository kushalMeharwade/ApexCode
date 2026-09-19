using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AiAssistant.Core.Services;

namespace AiAssistant.Engine.Services;

public class FileDependencyResolver : IFileDependencyResolver
{
    private readonly IVisualStudioEnvironmentService _vsEnvService;
    private readonly ISettingsService _settingsService;

    public FileDependencyResolver(IVisualStudioEnvironmentService vsEnvService, ISettingsService settingsService)
    {
        _vsEnvService = vsEnvService;
        _settingsService = settingsService;
    }

    public async Task<IEnumerable<string>> ResolveDependenciesAsync(IEnumerable<string> sourceFiles)
    {
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceList = sourceFiles.ToList();

        // Include original files first
        foreach (var file in sourceList)
        {
            resolved.Add(file);
        }

        var workspaceRoot = await _vsEnvService.GetWorkspaceRootAsync();
        if (string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot))
        {
            return resolved;
        }

        // Get all workspace files for type inference matching
        var extensions = _settingsService.WorkspaceExtensions.Split(',')
            .Select(e => e.Trim())
            .Where(e => !string.IsNullOrEmpty(e))
            .ToArray();

        var workspaceFiles = Directory.GetFiles(workspaceRoot, "*", SearchOption.AllDirectories)
            .Where(f => extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && !f.Contains("\\node_modules\\") && !f.Contains("\\.git\\"))
            .ToList();

        // Build dictionary of FilenameWithoutExtension -> List of full paths
        var fileMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var wf in workspaceFiles)
        {
            var name = Path.GetFileNameWithoutExtension(wf);
            if (!fileMap.ContainsKey(name))
                fileMap[name] = new List<string>();
            fileMap[name].Add(wf);
        }

        foreach (var file in sourceList)
        {
            if (!File.Exists(file)) continue;

            // 1. Companion Files
            ResolveCompanionFiles(file, resolved);

            // 2. Type Inference (Using Heuristic)
            try
            {
                var content = File.ReadAllText(file);
                // Extract words starting with uppercase (potential type names)
                var matches = Regex.Matches(content, @"\b[A-Z][a-zA-Z0-9_]*\b");
                
                var potentialTypes = matches.Cast<Match>()
                    .Select(m => m.Value)
                    .Distinct()
                    .ToList();

                int addedFromInference = 0;
                foreach (var typeName in potentialTypes)
                {
                    if (fileMap.TryGetValue(typeName, out var matchingPaths))
                    {
                        foreach (var matchPath in matchingPaths)
                        {
                            if (resolved.Add(matchPath))
                            {
                                addedFromInference++;
                                // Limit to prevent context explosion
                                if (addedFromInference >= 10) break;
                            }
                        }
                    }
                    if (addedFromInference >= 10) break;
                }
            }
            catch
            {
                // Ignore read errors
            }
        }

        return resolved;
    }

    private void ResolveCompanionFiles(string filePath, HashSet<string> resolved)
    {
        if (filePath.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase))
        {
            var xaml = filePath.Substring(0, filePath.Length - 3);
            if (File.Exists(xaml)) resolved.Add(xaml);
        }
        else if (filePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            var cs = filePath + ".cs";
            if (File.Exists(cs)) resolved.Add(cs);
        }
        else if (filePath.EndsWith(".razor.cs", StringComparison.OrdinalIgnoreCase))
        {
            var razor = filePath.Substring(0, filePath.Length - 3);
            if (File.Exists(razor)) resolved.Add(razor);
        }
        else if (filePath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
        {
            var cs = filePath + ".cs";
            if (File.Exists(cs)) resolved.Add(cs);
        }
    }
}
