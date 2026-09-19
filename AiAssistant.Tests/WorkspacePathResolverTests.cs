using System;
using System.IO;
using AiAssistant.Tools.Functions;
using Xunit;

namespace AiAssistant.Tests;

public class WorkspacePathResolverTests
{
    private const string Ws = @"C:\Work\MySolution";

    [Fact]
    public void ResolveWorkspacePath_ThrowsOnNullRoot()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorkspacePathResolver.ResolveWorkspacePath("src/Program.cs", null!));
        Assert.Equal("No workspace is open.", ex.Message);
    }

    [Fact]
    public void ResolveWorkspacePath_ThrowsOnEmptyRoot()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorkspacePathResolver.ResolveWorkspacePath("src/Program.cs", ""));
        Assert.Equal("No workspace is open.", ex.Message);
    }

    [Fact]
    public void ResolveWorkspacePath_ThrowsOnWhitespaceRoot()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorkspacePathResolver.ResolveWorkspacePath("src/Program.cs", "   "));
        Assert.Equal("No workspace is open.", ex.Message);
    }

    [Fact]
    public void ResolveWorkspacePath_PathTraversalOutsideWorkspace_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorkspacePathResolver.ResolveWorkspacePath("../../../etc/passwd", Ws));
        Assert.Contains("escapes workspace", ex.Message);
    }

    [Fact]
    public void ResolveWorkspacePath_ForwardSlashNormalized()
    {
        var result = WorkspacePathResolver.ResolveWorkspacePath("src/app/Program.cs", Ws);
        Assert.Equal(Path.Combine(Ws, "src", "app", "Program.cs"), result);
    }

    [Fact]
    public void ResolveWorkspacePath_BackslashNormalized()
    {
        var result = WorkspacePathResolver.ResolveWorkspacePath(@"src\app\Program.cs", Ws);
        Assert.Equal(Path.Combine(Ws, "src", "app", "Program.cs"), result);
    }

    [Fact]
    public void ResolveWorkspacePath_TrailingSlashTrimmed()
    {
        var result = WorkspacePathResolver.ResolveWorkspacePath("src/Program.cs", Ws + "\\");
        Assert.StartsWith(Ws, result);
    }

    [Fact]
    public void ResolveWorkspacePath_StripsGitHubPrefix()
    {
        // GITHUB_LATEST_CODE prefix — the LLM passes it literally, not as an absolute path
        var withPrefix = "GITHUB_LATEST_CODE/src/Program.cs";
        var result = WorkspacePathResolver.ResolveWorkspacePath(withPrefix, Ws);
        // After normalization: p = "GITHUB_LATEST_CODE\src\Program.cs"
        // After stripping prefix "GITHUB_LATEST_CODE": p = "\src\Program.cs"
        // After TrimStart: p = "src\Program.cs"
        // Final: C:\Work\MySolution\src\Program.cs
        Assert.Equal(Path.Combine(Ws, "src", "Program.cs"), result);
    }

    [Fact]
    public void ResolveWorkspacePath_AbsolutePathChecked()
    {
        var absPath = Path.Combine(Ws, "src", "Program.cs");
        var result = WorkspacePathResolver.ResolveWorkspacePath(absPath, Ws);
        Assert.Equal(absPath, result);
    }

    [Fact]
    public void ResolveWorkspacePath_CaseInsensitiveMatch()
    {
        var result = WorkspacePathResolver.ResolveWorkspacePath("Src/App/Program.cs", Ws);
        Assert.Equal(Path.Combine(Ws, "Src", "App", "Program.cs"), result);
    }

    [Fact]
    public void ToRelativePath_ConvertsToForwardSlashes()
    {
        var abs = Path.Combine(Ws, "src", "app", "Program.cs");
        var rel = WorkspacePathResolver.ToRelativePath(abs, Ws);
        Assert.Equal("src/app/Program.cs", rel);
    }

    [Fact]
    public void ToRelativePath_OutsideWorkspace_ReturnsForwardSlashVersion()
    {
        var outside = @"C:\Other\file.txt";
        var rel = WorkspacePathResolver.ToRelativePath(outside, Ws);
        Assert.Equal(@"C:\Other\file.txt".Replace('\\', '/'), rel);
    }

    [Fact]
    public void ToRelativePath_NullRoot_ReturnsForwardSlashVersion()
    {
        var abs = Path.Combine(Ws, "src", "Program.cs");
        var rel = WorkspacePathResolver.ToRelativePath(abs, null!);
        Assert.Equal(abs.Replace('\\', '/'), rel);
    }

    [Fact]
    public void ToRelativePath_EmptyRoot_ReturnsForwardSlashVersion()
    {
        var abs = Path.Combine(Ws, "src", "Program.cs");
        var rel = WorkspacePathResolver.ToRelativePath(abs, "");
        Assert.Equal(abs.Replace('\\', '/'), rel);
    }

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"C:\Work", false)]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"\\server\share\folder", false)]
    public void IsTooBroadRoot_DetectsDriveAndUNC(string root, bool expected)
    {
        if (string.IsNullOrEmpty(root)) { Assert.True(expected); return; }
        var normalized = Path.GetFullPath(root);
        var result = string.Equals(Path.GetPathRoot(normalized), normalized, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected, result);
    }
}
