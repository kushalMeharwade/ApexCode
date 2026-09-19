using AiAssistant.Core.Services;
using AiAssistant.Tools.Functions;
using Moq;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AiAssistant.Tests;

public class ReadFileLinesFunctionTests : IDisposable
{
    private readonly Mock<IVisualStudioEnvironmentService> _envMock;
    private readonly Mock<IOutputLogger> _loggerMock;
    private readonly Mock<ISettingsService> _settingsMock;
    private readonly Mock<IApprovalService> _approvalMock;
    private readonly Mock<IFileReadTracker> _fileReadTrackerMock;
    private readonly ReadFileLinesFunction _function;
    private readonly string _tempDir;

    public ReadFileLinesFunctionTests()
    {
        _envMock = new Mock<IVisualStudioEnvironmentService>();
        _loggerMock = new Mock<IOutputLogger>();
        _settingsMock = new Mock<ISettingsService>();
        _approvalMock = new Mock<IApprovalService>();

        _tempDir = Path.Combine(Path.GetTempPath(), "ReadFileTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);

        _envMock.Setup(e => e.GetWorkspaceRootAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_tempDir);
        _settingsMock.Setup(s => s.GetToolApprovalMode(It.IsAny<string>())).Returns("allowall");

        _fileReadTrackerMock = new Mock<IFileReadTracker>();

        _function = new ReadFileLinesFunction(_envMock.Object, _settingsMock.Object, _loggerMock.Object, _fileReadTrackerMock.Object, _approvalMock.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public async Task CreateFunction_ReadsFileContent()
    {
        var file = Path.Combine(_tempDir, "test.txt");
        File.WriteAllText(file, "Line 1\nLine 2");

        var aiFunction = _function.CreateFunction();
        
        // Execute the function dynamically since AIFunction wraps it
        var rawResult = await aiFunction.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments { { "filePaths", new[] { "test.txt" } }, { "endLine", 0 } }
        );
        var result = rawResult?.ToString();

        Assert.Contains("Line 1\nLine 2", result);
        Assert.Contains(file, result);
    }

    [Fact]
    public async Task CreateFunction_MaxLines_TruncatesContent()
    {
        var file = Path.Combine(_tempDir, "test.txt");
        File.WriteAllText(file, "Line 1\nLine 2\nLine 3\nLine 4");

        var aiFunction = _function.CreateFunction();
        
        var rawResult = await aiFunction.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments { { "filePaths", new[] { "test.txt" } }, { "endLine", 2 } }
        );
        var result = rawResult?.ToString();

        Assert.Contains("Line 1\nLine 2", result);
        Assert.DoesNotContain("Line 3", result);
    }

    [Fact]
    public async Task CreateFunction_FileNotFound_ReturnsErrorMessage()
    {
        var aiFunction = _function.CreateFunction();
        
        var rawResult = await aiFunction.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments { { "filePaths", new[] { "missing.txt" } }, { "endLine", 0 } }
        );
        var result = rawResult?.ToString();

        Assert.Contains("File not found", result);
    }

    [Fact]
    public async Task ReadsMultipleFiles_CommaSeparated()
    {
        var file1 = Path.Combine(_tempDir, "test1.txt");
        var file2 = Path.Combine(_tempDir, "test2.txt");
        File.WriteAllText(file1, "Content1");
        File.WriteAllText(file2, "Content2");

        var aiFunction = _function.CreateFunction();
        var rawResult = await aiFunction.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments { { "filePaths", new[] { "test1.txt", "test2.txt" } }, { "endLine", 0 } }
        );
        var result = rawResult?.ToString();

        Assert.Contains("Content1", result);
        Assert.Contains("Content2", result);
    }

    [Fact]
    public async Task PathTraversal_ReturnsJailError()
    {
        var aiFunction = _function.CreateFunction();
        var rawResult = await aiFunction.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments { { "filePaths", new[] { @"..\outside.txt" } }, { "endLine", 0 } }
        );
        var result = rawResult?.ToString();

        Assert.Contains("Path traversal denied", result);
    }

    [Fact]
    public async Task Exact10MBBoundary_Allowed()
    {
        var file = Path.Combine(_tempDir, "10mb.txt");
        using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(10 * 1024 * 1024);
        }

        var aiFunction = _function.CreateFunction();
        var rawResult = await aiFunction.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments { { "filePaths", new[] { "10mb.txt" } }, { "endLine", 0 } }
        );
        var result = rawResult?.ToString();

        Assert.DoesNotContain("File too large", result);
    }

    [Fact]
    public async Task Over10MBBoundary_Rejected()
    {
        var file = Path.Combine(_tempDir, "large.txt");
        using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(10 * 1024 * 1024 + 1);
        }

        var aiFunction = _function.CreateFunction();
        var rawResult = await aiFunction.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments { { "filePaths", new[] { "large.txt" } }, { "endLine", 0 } }
        );
        var result = rawResult?.ToString();

        Assert.Contains("File too large", result);
    }

    [Fact]
    public async Task MaxLinesZero_NoLimit()
    {
        var file = Path.Combine(_tempDir, "test.txt");
        File.WriteAllText(file, "Line 1\nLine 2\nLine 3\nLine 4");

        var aiFunction = _function.CreateFunction();
        var rawResult = await aiFunction.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments { { "filePaths", new[] { "test.txt" } }, { "endLine", 0 } }
        );
        var result = rawResult?.ToString();

        Assert.Contains("Line 4", result);
    }

    [Fact]
    public async Task TokenCap_TruncatesLargeFile()
    {
        var file = Path.Combine(_tempDir, "large_text.txt");
        var content = new StringBuilder();
        for (int i = 1; i <= 500; i++)
        {
            content.AppendLine($"Line {i}");
        }
        File.WriteAllText(file, content.ToString());

        var aiFunction = _function.CreateFunction();
        var rawResult = await aiFunction.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments { { "filePaths", new[] { "large_text.txt" } }, { "endLine", 0 } }
        );
        var result = rawResult?.ToString();

        Assert.Contains("call again with startLine=301", result);
        Assert.Contains("Returned lines 1–300", result);
    }

    [Fact]
    public async Task CacheHit_OnUnchangedFile()
    {
        var file = Path.Combine(_tempDir, "cache.txt");
        File.WriteAllText(file, "Initial content");

        var aiFunction = _function.CreateFunction();
        await aiFunction.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments { { "filePaths", new[] { "cache.txt" } }, { "endLine", 0 } }
        );

        await aiFunction.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments { { "filePaths", new[] { "cache.txt" } }, { "endLine", 0 } }
        );

        _loggerMock.Verify(l => l.Log(It.Is<string>(s => s != null && s.Contains("Cache hit"))), Times.AtLeastOnce);
    }

    [Fact]
    public async Task CacheInvalidation_OnWrite()
    {
        var file = Path.Combine(_tempDir, "cache_inv.txt");
        File.WriteAllText(file, "Initial content");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-5));

        var aiFunction = _function.CreateFunction();
        await aiFunction.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments { { "filePaths", new[] { "cache_inv.txt" } }, { "endLine", 0 } }
        );

        File.WriteAllText(file, "Updated content");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow);

        var rawResult = await aiFunction.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments { { "filePaths", new[] { "cache_inv.txt" } }, { "endLine", 0 } }
        );
        var result = rawResult?.ToString();

        Assert.Contains("Updated content", result);
    }

    [Fact]
    public async Task MissingWorkspaceRoot_ReturnsError()
    {
        _envMock.Setup(e => e.GetWorkspaceRootAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        var aiFunction = _function.CreateFunction();
        var rawResult = await aiFunction.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments { { "filePaths", new[] { "test.txt" } }, { "endLine", 0 } }
        );
        var result = rawResult?.ToString();

        Assert.Contains("No active workspace found", result);
    }

    [Fact]
    public async Task EmptyFilePaths_GracefulError()
    {
        var aiFunction = _function.CreateFunction();
        var rawResult = await aiFunction.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments { { "filePaths", Array.Empty<string>() }, { "endLine", 0 } }
        );
        var result = rawResult?.ToString();

        Assert.Contains("filePaths argument is missing", result);
    }

    private async System.Collections.Generic.IAsyncEnumerable<AiAssistant.Core.Models.ApprovalRequest> MockApprovalStream(AiAssistant.Core.Models.ApprovalRequest req)
    {
        yield return req;
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ApprovalMode_RequiresApproval()
    {
        _settingsMock.Setup(s => s.GetToolApprovalMode("read_files")).Returns("prompt");
        
        var requestMock = new AiAssistant.Core.Models.ApprovalRequest 
        { 
            Id = "req-1",
            ToolName = "read_files",
            Description = "Read file(s): test.txt"
        };
        
        _approvalMock.Setup(a => a.RequestApprovalAsync("read_files", It.IsAny<string>(), It.IsAny<object>()))
            .ReturnsAsync(requestMock);
            
        _approvalMock.Setup(a => a.WaitForApprovalAsync("req-1", It.IsAny<System.Threading.CancellationToken>()))
            .Returns(MockApprovalStream(new AiAssistant.Core.Models.ApprovalRequest { Id = "req-1", IsRejected = true }));

        var aiFunction = _function.CreateFunction();
        var rawResult = await aiFunction.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments { { "filePaths", new[] { "test.txt" } }, { "endLine", 0 } }
        );
        var result = rawResult?.ToString();

        Assert.Contains("USER REJECTED THIS ACTION", result);
    }
}

