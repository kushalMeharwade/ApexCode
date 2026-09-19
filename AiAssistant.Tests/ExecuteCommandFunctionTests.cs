using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Models;
using AiAssistant.Core.Services;
using AiAssistant.Tools.Functions;
using AiAssistant.Tools.Services;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace AiAssistant.Tests;

public class ExecuteCommandFunctionTests : IDisposable
{
    private readonly Mock<IVisualStudioEnvironmentService> _mockVsEnv;
    private readonly Mock<ISettingsService> _mockSettings;
    private readonly Mock<IApprovalService> _mockApproval;
    private readonly Mock<IOutputLogger> _mockLogger;
    private readonly string _testWorkspace;
    private readonly CommandSessionService _sessions = new();

    public ExecuteCommandFunctionTests()
    {
        _mockVsEnv = new Mock<IVisualStudioEnvironmentService>();
        _mockSettings = new Mock<ISettingsService>();
        _mockApproval = new Mock<IApprovalService>();
        _mockLogger = new Mock<IOutputLogger>();

        // Setup safe temporary workspace
        _testWorkspace = Path.Combine(Path.GetTempPath(), "AiAssistantCommandTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testWorkspace);

        _mockVsEnv.Setup(v => v.GetWorkspaceRootAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(_testWorkspace);
                  
        _mockSettings.Setup(s => s.GetToolApprovalMode("execute_command")).Returns("require_approval");
        _mockSettings.Setup(s => s.ExecuteCommandWhitelist).Returns(new List<string>());
    }

    public void Dispose()
    {
        _sessions.Dispose();
        if (Directory.Exists(_testWorkspace))
        {
            try { Directory.Delete(_testWorkspace, true); } catch { }
        }
    }

    private AIFunction CreateSut(bool useApproval = true)
    {
        if (!useApproval)
            _mockSettings.Setup(s => s.ExecuteCommandWhitelist).Returns(new List<string> {
                "echo ", "dir", "ping ", "set /p ", "this_is_an_invalid_command_123"
            });
        var provider = new ExecuteCommandFunction(
            _mockVsEnv.Object,
            _mockSettings.Object,
            useApproval ? _mockApproval.Object : null,
            _mockLogger.Object, _sessions);
        return provider.CreateFunction();
    }
    
    #pragma warning disable CS1998
    private async IAsyncEnumerable<T> ToAsyncEnumerable<T>(params T[] items)
    {
        foreach (var item in items)
        {
            yield return item;
        }
    }
    #pragma warning restore CS1998

    [Fact]
    public async Task ExecuteCommandAsync_WhitelistedCommand_ExecutesWithoutApproval()
    {
        // A-EC-001
        _mockSettings.Setup(s => s.GetToolApprovalMode("execute_command")).Returns("allowall");
        var sut = CreateSut();
        
        _mockSettings.Setup(s => s.ExecuteCommandWhitelist).Returns(new List<string> { "echo " });
        
        var args = new Microsoft.Extensions.AI.AIFunctionArguments
        {
            { "command", "echo hello" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("hello", result);
        
        _mockApproval.Verify(a => a.RequestApprovalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object?>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteCommandAsync_NonWhitelistedCommand_RequiresApproval()
    {
        // A-EC-002
        var sut = CreateSut();
        
        var mockReq = new ApprovalRequest { Id = "req-1" };
        _mockApproval.Setup(a => a.RequestApprovalAsync("execute_command", It.IsAny<string>(), It.IsAny<object?>()))
                     .ReturnsAsync(mockReq);
        _mockApproval.Setup(a => a.WaitForApprovalAsync("req-1", It.IsAny<CancellationToken>()))
                     .Returns(ToAsyncEnumerable(new ApprovalRequest { Id = "req-1", IsApproved = true }));
        
        var args = new Microsoft.Extensions.AI.AIFunctionArguments
        {
            { "command", "echo test-approval" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("test-approval", result);
        
        _mockApproval.Verify(a => a.RequestApprovalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object?>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteCommandAsync_NonWhitelistedCommand_ApprovalRejected_HaltsExecution()
    {
        // A-EC-003
        var sut = CreateSut();
        
        var mockReq = new ApprovalRequest { Id = "req-2" };
        _mockApproval.Setup(a => a.RequestApprovalAsync("execute_command", It.IsAny<string>(), It.IsAny<object?>()))
                     .ReturnsAsync(mockReq);
        _mockApproval.Setup(a => a.WaitForApprovalAsync("req-2", It.IsAny<CancellationToken>()))
                     .Returns(ToAsyncEnumerable(new ApprovalRequest { Id = "req-2", IsRejected = true }));
        
        var args = new Microsoft.Extensions.AI.AIFunctionArguments
        {
            { "command", "echo test-reject" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("USER REJECTED THIS ACTION", result);
        Assert.DoesNotContain("test-reject", result);
    }

    [Fact]
    public async Task ExecuteCommandAsync_EchoTest_CapturesStdout()
    {
        // A-EC-004
        _mockSettings.Setup(s => s.GetToolApprovalMode("execute_command")).Returns("allowall");
        var sut = CreateSut(useApproval: false);
        
        var args = new Microsoft.Extensions.AI.AIFunctionArguments
        {
            { "command", "echo capture_this" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("capture_this", result);
    }

    [Fact]
    public async Task ExecuteCommandAsync_InvalidCommand_CapturesStderr()
    {
        // A-EC-005
        _mockSettings.Setup(s => s.GetToolApprovalMode("execute_command")).Returns("allowall");
        var sut = CreateSut(useApproval: false);
        
        var args = new Microsoft.Extensions.AI.AIFunctionArguments
        {
            { "command", "this_is_an_invalid_command_123" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("[STDERR]", result);
        Assert.Contains("this_is_an_invalid_command_123", result);
    }

    [Fact]
    public async Task ExecuteCommandAsync_Timeout_KillsLongRunningProcess()
    {
        _mockSettings.Setup(s => s.GetToolApprovalMode("execute_command")).Returns("allowall");
        var sut = CreateSut(useApproval: false);
        
        var args = new Microsoft.Extensions.AI.AIFunctionArguments
        {
            { "command", "ping 127.0.0.1 -n 40" },
            { "timeout_seconds", 1 }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("timed_out", result);
    }

    [Fact]
    public async Task ExecuteCommandAsync_InteractiveInputIsClosed()
    {
        _mockSettings.Setup(s => s.GetToolApprovalMode("execute_command")).Returns("allowall");
        var sut = CreateSut(useApproval: false);
        
        var args = new Microsoft.Extensions.AI.AIFunctionArguments
        {
            { "command", "set /p unanswered=" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.DoesNotContain("\"status\":\"running\"", result);
    }

    [Fact]
    public async Task ExecuteCommandAsync_MissingCommandArg_ReturnsError()
    {
        // A-EC-008
        var sut = CreateSut();
        
        var args = new Microsoft.Extensions.AI.AIFunctionArguments();

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("Error: command argument is missing", result);
    }

    [Fact]
    public async Task ExecuteCommandAsync_ApprovalStreamEndsWithoutDecision_DoesNotExecute()
    {
        _mockApproval.Setup(a => a.RequestApprovalAsync("execute_command", It.IsAny<string>(), It.IsAny<object?>()))
            .ReturnsAsync(new ApprovalRequest { Id = "empty" });
        _mockApproval.Setup(a => a.WaitForApprovalAsync("empty", It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable<ApprovalRequest>());
        var result = await CreateSut().InvokeAsync(new AIFunctionArguments { ["command"] = "echo forbidden" });
        Assert.Contains("without an explicit decision", result.ToString());
    }

    [Fact]
    public async Task ExecuteCommandAsync_WorkingDirectory_IsCorrect()
    {
        // A-EC-009
        _mockSettings.Setup(s => s.GetToolApprovalMode("execute_command")).Returns("allowall");
        var sut = CreateSut(useApproval: false);
        
        File.WriteAllText(Path.Combine(_testWorkspace, "marker_file.txt"), "hello");
        
        var args = new Microsoft.Extensions.AI.AIFunctionArguments
        {
            { "command", "dir" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("marker_file.txt", result);
    }

    [Fact]
    public async Task ExecuteCommandAsync_WhitelistBypass_WithMaliciousChain_RequiresApproval()
    {
        // A-EC-010
        var sut = CreateSut();
        
        _mockSettings.Setup(s => s.ExecuteCommandWhitelist).Returns(new List<string> { "echo " });
        
        var mockReq = new ApprovalRequest { Id = "req-malicious" };
        _mockApproval.Setup(a => a.RequestApprovalAsync("execute_command", It.IsAny<string>(), It.IsAny<object?>()))
                     .ReturnsAsync(mockReq);
        _mockApproval.Setup(a => a.WaitForApprovalAsync("req-malicious", It.IsAny<CancellationToken>()))
                     .Returns(ToAsyncEnumerable(new ApprovalRequest { Id = "req-malicious", IsRejected = true }));
        
        var args = new Microsoft.Extensions.AI.AIFunctionArguments
        {
            // Starts with "echo " (whitelisted) but contains "rm -rf" (malicious list)
            { "command", "echo test && rm -rf /" }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("USER REJECTED THIS ACTION", result);
        
        // Assert approval was requested because malicious detection overrode whitelist
        _mockApproval.Verify(a => a.RequestApprovalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object?>()), Times.Once);
    }
}

