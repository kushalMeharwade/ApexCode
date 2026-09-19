using System;
using AiAssistant.Tools.Functions;
using AiAssistant.Core.Services;
using Moq;
using Xunit;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

namespace AiAssistant.Tests;

public class ToolFunctionTests
{
    private readonly Mock<ISettingsService> _mockSettings = new Mock<ISettingsService>();
    private readonly Mock<IOutputLogger> _mockLogger = new Mock<IOutputLogger>();
    private readonly Mock<IApprovalService> _mockApproval = new Mock<IApprovalService>();
    private readonly Mock<IVisualStudioEnvironmentService> _mockVsEnv = new Mock<IVisualStudioEnvironmentService>();
    private readonly Mock<IFileReadTracker> _mockFileReadTracker = new Mock<IFileReadTracker>();

    [Fact]
    public void ReadFileLinesFunction_PropertiesAreCorrect()
    {
        var func = new ReadFileLinesFunction(_mockVsEnv.Object, _mockSettings.Object, _mockLogger.Object, _mockFileReadTracker.Object, _mockApproval.Object);
        Assert.Equal("read_file_lines", func.CreateFunction().Name);
        Assert.NotEmpty(func.CreateFunction().Description);
    }

    [Fact]
    public void CreateFileFunction_PropertiesAreCorrect()
    {
        var func = new CreateFileFunction(_mockSettings.Object, _mockVsEnv.Object, _mockApproval.Object, _mockLogger.Object);
        Assert.Equal("create_file", func.CreateFunction().Name);
    }

    [Fact]
    public void SearchCodeFunction_PropertiesAreCorrect()
    {
        var func = new SearchCodeFunction(_mockVsEnv.Object, null, null, null);
        Assert.Equal("search_codebase", func.CreateFunction().Name);
    }

    [Fact]
    public void ReplaceFileFunction_PropertiesAreCorrect()
    {
        var mockTx = new Mock<ITransactionSnapshotService>();
        var func = new ReplaceFileFunction(_mockSettings.Object, _mockVsEnv.Object, mockTx.Object, _mockFileReadTracker.Object, _mockApproval.Object, _mockLogger.Object);
        Assert.Equal("replace_in_file", func.CreateFunction().Name);
    }



    [Fact]
    public void ExecuteCommandFunction_PropertiesAreCorrect()
    {
        var func = new ExecuteCommandFunction(_mockVsEnv.Object, _mockSettings.Object, _mockApproval.Object, _mockLogger.Object);
        Assert.Equal("execute_command", func.CreateFunction().Name);
    }

    [Fact]
    public void GetDiagnosticsFunction_PropertiesAreCorrect()
    {
        var func = new GetDiagnosticsFunction(_mockVsEnv.Object);
        Assert.Equal("get_diagnostics", func.CreateFunction().Name);
    }
}
