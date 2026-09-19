using AiAssistant.Engine.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Xunit;

namespace AiAssistant.Tests;

public class RoslynEditServiceTests : IDisposable
{
    private readonly RoslynEditService _service;
    private readonly string _tempDir;

    public RoslynEditServiceTests()
    {
        _service = new RoslynEditService();
        _tempDir = Path.Combine(Path.GetTempPath(), "RoslynEditTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private string CreateTempFile(string content, string ext = ".txt")
    {
        var file = Path.Combine(_tempDir, Guid.NewGuid() + ext);
        File.WriteAllText(file, content);
        return file;
    }

    [Fact]
    public async Task ApplyEditAsync_ReplacesTextCorrectly()
    {
        var file = CreateTempFile("Hello World");

        var result = await _service.ApplyEditAsync(file, "World", "Universe");

        Assert.True(result.Success);
        Assert.Equal("Hello Universe", result.NewContent);
        Assert.Equal("Hello Universe", File.ReadAllText(file));
    }

    [Fact]
    public async Task ApplyEditAsync_FileNotFound_ReturnsError()
    {
        var result = await _service.ApplyEditAsync("missing.txt", "old", "new");
        Assert.False(result.Success);
        Assert.Contains("File not found", result.ErrorMessage);
    }

    [Fact]
    public async Task ApplyEditAsync_TextNotFound_ReturnsError()
    {
        var file = CreateTempFile("Hello World");
        var result = await _service.ApplyEditAsync(file, "Mars", "Universe");
        Assert.False(result.Success);
        Assert.Contains("Text not found", result.ErrorMessage);
    }

    [Fact]
    public async Task ReplaceTextAsync_ValidBounds_ReplacesText()
    {
        var file = CreateTempFile("Line 1\nLine 2\nLine 3");
        // Replace "Line 2" (Line 2, Col 1 to Col 7) with "New Line"
        var result = await _service.ReplaceTextAsync(file, 2, 1, 2, 6, "New Line");

        Assert.True(result.Success);
        Assert.Contains("New Line", result.NewContent);
        Assert.Equal("Line 1\nNew Line\nLine 3", result.NewContent);
    }

    [Fact]
    public async Task ReplaceTextAsync_OutOfBoundsLine_ReturnsError()
    {
        var file = CreateTempFile("Line 1");
        var result = await _service.ReplaceTextAsync(file, 2, 1, 2, 5, "New");
        
        Assert.False(result.Success);
        Assert.Contains("out of range", result.ErrorMessage);
    }

    [Fact]
    public async Task GetFileContentAsync_ReturnsContent()
    {
        var file = CreateTempFile("content");
        var content = await _service.GetFileContentAsync(file);
        Assert.Equal("content", content);
    }

    [Fact]
    public async Task ApplyRoslynEditAsync_CSharpFile_EditsCode()
    {
        var file = CreateTempFile("class A { void M() { } }", ".cs");
        
        var result = await _service.ApplyRoslynEditAsync(file, "void M() { }", "void M() { int x = 1; }");
        
        Assert.True(result.Success);
        Assert.Contains("int x = 1;", result.NewContent);
    }

    [Fact]
    public async Task ApplyRoslynEditAsync_Fallback_WhenNotCSharp()
    {
        var file = CreateTempFile("old code", ".txt");
        var result = await _service.ApplyRoslynEditAsync(file, "old", "new");
        
        Assert.True(result.Success);
        Assert.Equal("new code", result.NewContent);
    }

    // ========== NEW TESTS FOR FIXES ==========

    [Fact]
    public void TryBuildBatchReplacement_MultipleEditsOnSameLine_WithoutMultiLine_Succeeds()
    {
        // Issue #2 fix: Multiple single-line edits on same line should work
        var content = "var x = 1; var y = 2;";
        var edits = new List<AiAssistant.Engine.Models.BatchEditRequest>
        {
            new() { OldText = "x", NewText = "a", ExpectedOccurrences = 1 },
            new() { OldText = "y", NewText = "b", ExpectedOccurrences = 1 }
        };

        var result = RoslynEditService.TryBuildBatchReplacement(content, edits, null, null);

        Assert.True(result.Success);
        Assert.Contains("var a = 1", result.NewContent);
        Assert.Contains("var b = 2", result.NewContent);
    }

    [Fact]
    public void TryBuildBatchReplacement_MultiLineReplacement_SingleEdit_Succeeds()
    {
        // Issue #2 fix: Single multi-line replacement should work
        var content = "line1\nline2\nline3";
        var edits = new List<AiAssistant.Engine.Models.BatchEditRequest>
        {
            new() { OldText = "line2", NewText = "new1\nnew2", ExpectedOccurrences = 1 }
        };

        var result = RoslynEditService.TryBuildBatchReplacement(content, edits, null, null);

        Assert.True(result.Success);
        Assert.Contains("new1", result.NewContent);
        Assert.Contains("new2", result.NewContent);
    }

    [Fact]
    public void TryBuildBatchReplacement_OverlappingEdits_ShouldFail()
    {
        // Overlapping edits should be detected and rejected
        var content = "Hello World";
        var edits = new List<AiAssistant.Engine.Models.BatchEditRequest>
        {
            new() { OldText = "Hello World", NewText = "Hi There", ExpectedOccurrences = 1 },
            new() { OldText = "World", NewText = "Universe", ExpectedOccurrences = 1 }
        };

        var result = RoslynEditService.TryBuildBatchReplacement(content, edits, null, null);

        Assert.False(result.Success);
        Assert.Equal("overlapping_edits", result.Reason);
    }

    [Fact]
    public void TryBuildReplacement_TierEscalation_WorksCorrectly()
    {
        // Test that tiered matching escalates through tiers
        var content = "    public void Method()\n    {\n        Console.WriteLine(\"test\");\n    }";
        var oldText = "public void Method()\n{\n    Console.WriteLine(\"test\");\n}"; // Different baseline indentation

        var result = RoslynEditService.TryBuildReplacement(content, oldText, "// replaced", 1);

        Assert.True(result.Success);
        Assert.Contains("indentation", result.TiersAttempted);
        Assert.Contains("// replaced", result.NewContent);
    }

    [Fact]
    public void TryBuildReplacement_NoMatchAnyTier_ReturnsClosestMatch()
    {
        // Test that closest match is returned when no tier succeeds
        var content = "public void SomeMethod() { }";
        var oldText = "public void OtherMethod() { }"; // Won't match

        var result = RoslynEditService.TryBuildReplacement(content, oldText, "replaced", 1);

        Assert.False(result.Success);
        Assert.Equal("no_match_any_tier", result.Reason);
        Assert.NotNull(result.ClosestMatch);
    }

    [Fact]
    public void TryBuildReplacement_WhitespaceNormalization_Succeeds()
    {
        // Tier 1: Whitespace normalization should handle CRLF vs LF
        var content = "line1\r\nline2\r\nline3";
        var oldText = "line1\nline2\nline3"; // Different line endings

        var result = RoslynEditService.TryBuildReplacement(content, oldText, "replaced", 1);

        Assert.True(result.Success);
        Assert.Contains("whitespace", result.TiersAttempted);
    }

    [Fact]
    public void TryBuildReplacement_IndentationRelativeFuzzy_PreservesIndentation()
    {
        // Tier 2: Should preserve file's actual indentation
        var content = "    if (x > 0)\n    {\n        DoSomething();\n    }";
        var oldText = "if (x > 0)\n{\n    DoSomething();\n}"; // No leading indent
        var newText = "if (x > 5)\n{\n    DoOtherThing();\n}";

        var result = RoslynEditService.TryBuildReplacement(content, oldText, newText, 1);

        Assert.True(result.Success);
        // Check that the result maintains the 4-space indentation
        Assert.Contains("    if (x > 5)", result.NewContent);
        Assert.Contains("        DoOtherThing()", result.NewContent);
    }

    [Fact]
    public void TryBuildReplacement_RoslynStructuralMatch_WorksForCSharp()
    {
        // Tier 3: Roslyn-based structural matching
        var content = @"
class TestClass
{
    public void Method()
    {
        Console.WriteLine(""test"");
    }
}";
        var oldText = "public void Method()\n{\n    Console.WriteLine(\"test\");\n}";
        var newText = "public void Method()\n{\n    Console.WriteLine(\"updated\");\n}";

        var result = RoslynEditService.TryBuildReplacement(content, oldText, newText, 1);

        Assert.True(result.Success);
        Assert.Contains("updated", result.NewContent);
    }

    [Fact]
    public void TryBuildReplacement_ExpectedOccurrences_Validation()
    {
        // Test occurrence validation
        var content = "foo bar foo baz foo";
        var oldText = "foo";
        var newText = "replaced";

        var result = RoslynEditService.TryBuildReplacement(content, oldText, newText, 2);

        Assert.False(result.Success);
        Assert.Equal("occurrence_mismatch", result.Reason);
        Assert.Equal(2, result.ExpectedOccurrences);
        Assert.Equal(3, result.FoundOccurrences);
        Assert.NotNull(result.MatchPreviews);
        Assert.Equal(3, result.MatchPreviews.Count);
    }

    [Fact]
    public void TryBuildBatchReplacement_ScopeOptimization_WorksForLargeFiles()
    {
        // Test scope optimization with line ranges
        var lines = new System.Text.StringBuilder();
        for (int i = 1; i <= 1000; i++)
        {
            lines.AppendLine($"Line {i}");
        }
        lines.Append("Target Line"); // Line 1001
        for (int i = 1002; i <= 2000; i++)
        {
            lines.AppendLine($"Line {i}");
        }

        var content = lines.ToString();
        var edits = new List<AiAssistant.Engine.Models.BatchEditRequest>
        {
            new() { OldText = "Target Line", NewText = "Replaced Line", ExpectedOccurrences = 1 }
        };

        // Scope to lines 900-1100 (includes target at line 1001)
        var result = RoslynEditService.TryBuildBatchReplacement(content, edits, scopeStartLine: 900, scopeEndLine: 1100);

        Assert.True(result.Success);
        Assert.Contains("Replaced Line", result.NewContent);
    }
}
