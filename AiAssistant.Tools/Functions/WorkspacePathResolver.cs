using System;
using System.IO;

namespace AiAssistant.Tools.Functions;

/// <summary>
/// Centralized utility class to normalize paths, strip known invalid prefixes 
/// (like GITHUB_LATEST_CODE), block traversal outside the workspace, 
/// and format output paths relatively.
/// </summary>
public static class WorkspacePathResolver
{
    /// <summary>
    /// Normalizes slashes, trims invalid prefixes, applies PathJail containment,
    /// and returns the absolute path inside the workspace.
    /// </summary>
    public static string ResolveWorkspacePath(string input, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Path cannot be empty.", nameof(input));

        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new InvalidOperationException("No workspace is open.");

        // 1. Normalize forward slashes to platform separator
        string p = input.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

        // 2. Strip known prefixes the LLM might erroneously include
        string[] prefixes = new[]
        {
            workspaceRoot,
            "GITHUB_LATEST_CODE",
            Path.Combine(workspaceRoot, "GITHUB_LATEST_CODE")
        };

        foreach (var prefix in prefixes)
        {
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                p = p.Substring(prefix.Length);
                break;
            }
        }

        // 3. Trim leading separators
        p = p.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 4. Reject traversal outside the workspace (security)
        string resolved = Path.GetFullPath(Path.Combine(workspaceRoot, p));
        if (!PathJail.IsContained(resolved, workspaceRoot))
        {
            throw new InvalidOperationException($"Path escapes workspace: {input}");
        }

        return resolved;
    }

    /// <summary>
    /// Converts absolute paths back to relative ones with forward slashes for output.
    /// Used for consistent formatting in tool output strings.
    /// </summary>
    public static string ToRelativePath(string absolutePath, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return absolutePath;

        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return absolutePath.Replace('\\', '/');

        var normalizedFull = Path.GetFullPath(absolutePath);
        var normalizedRoot = Path.GetFullPath(workspaceRoot);

        if (!normalizedRoot.EndsWith(Path.DirectorySeparatorChar.ToString()))
            normalizedRoot += Path.DirectorySeparatorChar;

        if (normalizedFull.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            string rel = normalizedFull.Substring(normalizedRoot.Length);
            return rel.Replace('\\', '/');
        }

        // If not contained, just return the normalized forward-slash version of the full path
        return normalizedFull.Replace('\\', '/');
    }
}


