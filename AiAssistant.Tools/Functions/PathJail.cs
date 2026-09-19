using System;
using System.IO;

namespace AiAssistant.Tools.Functions;

/// <summary>
/// Centralised path-containment check used by all file-write and file-read tools.
/// Prevents path-traversal attacks where a model-supplied path escapes the workspace root.
///
/// Key correctness properties:
///   1. Appends a trailing separator before the prefix test so that
///      C:\repo never admits C:\repo-secrets (plain StartsWith fails this).
///   2. The check is non-optional: approval mode controls *intent*, not containment.
///      Even "allowall" must not bypass the workspace boundary.
/// </summary>
internal static class PathJail
{
    /// <summary>
    /// Returns <c>true</c> if <paramref name="fullPath"/> is inside
    /// <paramref name="workspaceRoot"/> (inclusive of the root itself).
    /// Both paths are normalised with <see cref="Path.GetFullPath"/> before comparison.
    /// </summary>
    public static bool IsContained(string fullPath, string workspaceRoot)
    {
        if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(workspaceRoot))
            return false;

        var normalizedFull = Path.GetFullPath(fullPath);
        var normalizedRoot = Path.GetFullPath(workspaceRoot);

        // Ensure root has a trailing separator so "C:\repo" does NOT match "C:\repo-secrets"
        if (!normalizedRoot.EndsWith(Path.DirectorySeparatorChar.ToString()))
            normalizedRoot += Path.DirectorySeparatorChar;

        // Allow the root directory itself (exact match without trailing separator)
        var rootWithoutTrail = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar);
        return normalizedFull.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalizedFull, rootWithoutTrail, StringComparison.OrdinalIgnoreCase);
    }
}


