using System;
using System.Collections.Generic;
using System.IO;

namespace AiAssistant.Engine.Services;

/// <summary>
/// Resolves companion files for non-C# technologies (Angular, React, etc.).
/// Uses file naming conventions to discover related files.
/// </summary>
public class CompanionFileResolver
{
    /// <summary>
    /// Resolves companion files for a given file based on naming conventions.
    /// </summary>
    public List<ResolvedDependency> ResolveDependencies(string sourceFilePath)
    {
        var dependencies = new List<ResolvedDependency>();

        if (!File.Exists(sourceFilePath))
            return dependencies;

        var directory = Path.GetDirectoryName(sourceFilePath);
        if (string.IsNullOrEmpty(directory))
            return dependencies;

        var fileName = Path.GetFileName(sourceFilePath);
        var extension = Path.GetExtension(sourceFilePath).ToLowerInvariant();

        // Angular Component Pattern: component-name.component.[html|ts|css|scss|spec.ts]
        if (IsAngularComponentFile(fileName))
        {
            var baseName = GetAngularComponentBaseName(fileName);
            if (!string.IsNullOrEmpty(baseName))
            {
                AddAngularCompanionFiles(directory, baseName, sourceFilePath, dependencies);
            }
        }
        // React Component Pattern: ComponentName.[tsx|jsx] and ComponentName.module.[css|scss]
        else if (IsReactComponentFile(fileName))
        {
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            AddReactCompanionFiles(directory, baseName, sourceFilePath, dependencies);
        }
        // Vue Component Pattern: ComponentName.vue
        else if (fileName.EndsWith(".vue", StringComparison.OrdinalIgnoreCase))
        {
            // Vue components are typically single-file, but may have associated .ts files
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var tsFile = Path.Combine(directory, $"{baseName}.ts");
            if (File.Exists(tsFile))
            {
                dependencies.Add(CreateDependency(tsFile, "Vue component TypeScript file", 0.9f));
            }
        }
        // Generic HTML ↔ TypeScript/JavaScript
        else if (extension == ".html")
        {
            AddGenericHtmlCompanions(directory, fileName, sourceFilePath, dependencies);
        }
        // Generic TypeScript/JavaScript ↔ HTML
        else if (extension == ".ts" || extension == ".tsx" || extension == ".js" || extension == ".jsx")
        {
            AddGenericScriptCompanions(directory, fileName, sourceFilePath, dependencies);
        }
        // CSS/SCSS ↔ TypeScript/JavaScript
        else if (extension == ".css" || extension == ".scss" || extension == ".sass" || extension == ".less")
        {
            AddStyleCompanions(directory, fileName, sourceFilePath, dependencies);
        }

        return dependencies;
    }

    private bool IsAngularComponentFile(string fileName)
    {
        return fileName.IndexOf(".component.", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private string? GetAngularComponentBaseName(string fileName)
    {
        // Extract "add-salary-structure" from "add-salary-structure.component.html"
        var componentIndex = fileName.IndexOf(".component.", StringComparison.OrdinalIgnoreCase);
        if (componentIndex > 0)
        {
            return fileName.Substring(0, componentIndex);
        }
        return null;
    }

    private void AddAngularCompanionFiles(string directory, string baseName, string sourceFilePath, List<ResolvedDependency> dependencies)
    {
        // Angular component companion files
        var extensions = new[]
        {
            ".component.ts",
            ".component.html",
            ".component.css",
            ".component.scss",
            ".component.sass",
            ".component.less",
            ".component.spec.ts"
        };

        foreach (var ext in extensions)
        {
            var companionFile = Path.Combine(directory, baseName + ext);
            if (File.Exists(companionFile) && !companionFile.Equals(sourceFilePath, StringComparison.OrdinalIgnoreCase))
            {
                var reason = ext switch
                {
                    ".component.ts" => "Angular component TypeScript",
                    ".component.html" => "Angular component template",
                    ".component.css" or ".component.scss" or ".component.sass" or ".component.less" => "Angular component styles",
                    ".component.spec.ts" => "Angular component tests",
                    _ => "Angular component companion file"
                };

                dependencies.Add(CreateDependency(companionFile, reason, 0.95f));
            }
        }
    }

    private bool IsReactComponentFile(string fileName)
    {
        return fileName.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase) ||
               (fileName.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) && char.IsUpper(fileName[0])) ||
               (fileName.EndsWith(".js", StringComparison.OrdinalIgnoreCase) && char.IsUpper(fileName[0]));
    }

    private void AddReactCompanionFiles(string directory, string baseName, string sourceFilePath, List<ResolvedDependency> dependencies)
    {
        // React component companion files
        var extensions = new[]
        {
            ".module.css",
            ".module.scss",
            ".module.sass",
            ".css",
            ".scss",
            ".test.tsx",
            ".test.ts",
            ".spec.tsx",
            ".spec.ts"
        };

        foreach (var ext in extensions)
        {
            var companionFile = Path.Combine(directory, baseName + ext);
            if (File.Exists(companionFile) && !companionFile.Equals(sourceFilePath, StringComparison.OrdinalIgnoreCase))
            {
                var reason = ext.Contains("module") ? "React CSS Module" :
                            ext.Contains("test") || ext.Contains("spec") ? "React component tests" :
                            ext.Contains("css") || ext.Contains("scss") || ext.Contains("sass") ? "React component styles" :
                            "React component companion file";

                dependencies.Add(CreateDependency(companionFile, reason, 0.9f));
            }
        }
    }

    private void AddGenericHtmlCompanions(string directory, string fileName, string sourceFilePath, List<ResolvedDependency> dependencies)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);

        // Look for matching TypeScript/JavaScript files
        var scriptExtensions = new[] { ".ts", ".tsx", ".js", ".jsx" };
        foreach (var ext in scriptExtensions)
        {
            var scriptFile = Path.Combine(directory, baseName + ext);
            if (File.Exists(scriptFile))
            {
                dependencies.Add(CreateDependency(scriptFile, "Companion script file", 0.85f));
            }
        }
    }

    private void AddGenericScriptCompanions(string directory, string fileName, string sourceFilePath, List<ResolvedDependency> dependencies)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);

        // Look for matching HTML file
        var htmlFile = Path.Combine(directory, baseName + ".html");
        if (File.Exists(htmlFile))
        {
            dependencies.Add(CreateDependency(htmlFile, "Companion HTML template", 0.85f));
        }

        // Look for matching style files
        var styleExtensions = new[] { ".css", ".scss", ".sass", ".less" };
        foreach (var ext in styleExtensions)
        {
            var styleFile = Path.Combine(directory, baseName + ext);
            if (File.Exists(styleFile))
            {
                dependencies.Add(CreateDependency(styleFile, "Companion stylesheet", 0.85f));
            }
        }
    }

    private void AddStyleCompanions(string directory, string fileName, string sourceFilePath, List<ResolvedDependency> dependencies)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);

        // Look for matching TypeScript/JavaScript files
        var scriptExtensions = new[] { ".ts", ".tsx", ".js", ".jsx" };
        foreach (var ext in scriptExtensions)
        {
            var scriptFile = Path.Combine(directory, baseName + ext);
            if (File.Exists(scriptFile))
            {
                dependencies.Add(CreateDependency(scriptFile, "Component using this stylesheet", 0.85f));
            }
        }
    }

    private ResolvedDependency CreateDependency(string filePath, string reason, float confidence)
    {
        return new ResolvedDependency
        {
            FilePath = filePath,
            Confidence = confidence,
            Reason = reason,
            Tier = 2 // Companion files are Tier 2 (inferred, not explicit symbol references)
        };
    }
}
