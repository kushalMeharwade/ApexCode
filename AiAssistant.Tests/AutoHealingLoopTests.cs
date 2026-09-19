using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Engine.Models;
using AiAssistant.Engine.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AiAssistant.Tests;

public class AutoHealingLoopTests
{
    private readonly Mock<ICompileValidator> _validatorMock;
    private readonly Mock<IErrorFixer> _errorFixerMock;
    private readonly Mock<IRoslynEditService> _editServiceMock;
    private readonly Mock<ILogger<AutoHealingLoop>> _loggerMock;
    private readonly AutoHealingLoop _loop;

    public AutoHealingLoopTests()
    {
        _validatorMock = new Mock<ICompileValidator>();
        _errorFixerMock = new Mock<IErrorFixer>();
        _editServiceMock = new Mock<IRoslynEditService>();
        _loggerMock = new Mock<ILogger<AutoHealingLoop>>();

        _loop = new AutoHealingLoop(
            _editServiceMock.Object,
            _validatorMock.Object,
            _errorFixerMock.Object,
            _loggerMock.Object,
            maxIterations: 3);

        _editServiceMock.Setup(e => e.GetFileContentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("mock file content");
    }

    [Fact]
    public async Task RunAsync_FailsImmediately_IfFirstFixFails()
    {
        // Arrange
        _errorFixerMock.Setup(f => f.FixErrorAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ErrorFixResult { Success = false, ErrorMessage = "LLM failed" });

        var tempFile = Path.GetTempFileName() + ".cs";
        File.WriteAllText(tempFile, "test");
        try
        {
            // Act
            var result = await _loop.RunAsync("proj", tempFile, "initial error");

            // Assert
            Assert.False(result.Success);
            Assert.Equal(3, result.Iterations); // We try up to max iterations if fix fails
            _validatorMock.Verify(v => v.CompileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RunAsync_Succeeds_IfCompilationPassesAfterFix()
    {
        // Arrange
        _errorFixerMock.Setup(f => f.FixErrorAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ErrorFixResult { Success = true, FixedCode = "fixed code" });

        _editServiceMock.Setup(e => e.ReplaceEntireFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EditResult { Success = true });

        _validatorMock.Setup(v => v.CompileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompilationResult { Success = true });

        var tempFile = Path.GetTempFileName() + ".cs";
        File.WriteAllText(tempFile, "test");
        try
        {
            // Act
            var result = await _loop.RunAsync("proj", tempFile, "initial error");

            // Assert
            Assert.True(result.Success);
            Assert.Equal(1, result.Iterations);
            Assert.Null(result.ErrorMessage);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RunAsync_ExhaustsIterations_IfCompilationNeverPasses()
    {
        // Arrange
        _errorFixerMock.Setup(f => f.FixErrorAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ErrorFixResult { Success = true, FixedCode = "fixed code" });

        _editServiceMock.Setup(e => e.ReplaceEntireFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EditResult { Success = true });

        _validatorMock.Setup(v => v.CompileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompilationResult { Success = false, Errors = new[] { "still broken" } });

        var tempFile = Path.GetTempFileName() + ".cs";
        File.WriteAllText(tempFile, "test");
        try
        {
            // Act
            var result = await _loop.RunAsync("proj", tempFile, "initial error");

            // Assert
            Assert.False(result.Success);
            Assert.Equal(3, result.Iterations); // Should hit maxIterations
            Assert.Contains("still broken", result.ErrorMessage);
            
            // Verify we tried 3 times
            _errorFixerMock.Verify(f => f.FixErrorAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
            _validatorMock.Verify(v => v.CompileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
