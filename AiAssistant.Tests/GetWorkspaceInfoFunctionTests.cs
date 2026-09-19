using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Services;
using AiAssistant.Tools.Functions;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace AiAssistant.Tests;

public class GetWorkspaceInfoFunctionTests : IDisposable
{
    private readonly Mock<IVisualStudioEnvironmentService> _mockVsEnv;
    private readonly Mock<IOutputLogger> _mockLogger;
    private readonly string _testWorkspace;

    public GetWorkspaceInfoFunctionTests()
    {
        _mockVsEnv = new Mock<IVisualStudioEnvironmentService>();
        _mockLogger = new Mock<IOutputLogger>();

        // Setup safe temporary workspace
        _testWorkspace = Path.Combine(Path.GetTempPath(), "AiAssistantWorkspaceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testWorkspace);

        _mockVsEnv.Setup(v => v.GetWorkspaceRootAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(_testWorkspace);
                  
        _mockVsEnv.Setup(v => v.GetLoadedProjectsAsync())
                  .ReturnsAsync(new List<string>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_testWorkspace))
        {
            try { Directory.Delete(_testWorkspace, true); } catch { }
        }
    }

    private AIFunction CreateSut()
    {
        var provider = new GetWorkspaceInfoFunction(
            _mockVsEnv.Object,
            _mockLogger.Object);
        return provider.CreateFunction();
    }
    
    private void CreateTempFiles(IEnumerable<string> relativePaths)
    {
        foreach (var path in relativePaths)
        {
            var fullPath = Path.Combine(_testWorkspace, path.Replace('/', Path.DirectorySeparatorChar));
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(fullPath, "dummy");
        }
    }

    [Fact]
    public async Task OpenFolder_ReportsMixedFilesAndManifestsWithoutFileListing()
    {
        CreateTempFiles(new[] { "src/app.ts", "src/View.xaml", "src/App.csproj", "package.json", "OBJ/generated.cs", "node_modules/library/index.js" });
        var result = await CreateSut().InvokeAsync(new AIFunctionArguments(), CancellationToken.None) as string;
        Assert.Contains("Workspace Directory:** " + _testWorkspace, result);
        Assert.Contains("Workspace Files:** 4", result);
        Assert.Contains(".ts: 1", result);
        Assert.Contains(".xaml: 1", result);
        Assert.Contains("App.csproj", result);
        Assert.Contains("package.json", result);
        Assert.DoesNotContain("### Source Files", result);
    }

    [Fact]
    public async Task NoWorkspace_DoesNotScanCurrentDirectoryOrQueryProjects()
    {
        _mockVsEnv.Setup(v => v.GetWorkspaceRootAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string)null);
        var result = await CreateSut().InvokeAsync(new AIFunctionArguments(), CancellationToken.None) as string;
        Assert.DoesNotContain("C# Files:", result);
        _mockVsEnv.Verify(v => v.GetLoadedProjectsAsync(), Times.Never);
    }

    [Fact]
    public async Task CancelledScan_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await CreateSut().InvokeAsync(new AIFunctionArguments(), cancellation.Token));
    }

    [Fact]
    public async Task GetWorkspaceInfoAsync_IncludeFilesFalse_ReturnsProjectsOnly()
    {
        // A-WI-001
        var sut = CreateSut();
        
        var projects = new List<string> { "ProjA.csproj", "ProjB.csproj", "ProjC.csproj" };
        _mockVsEnv.Setup(v => v.GetLoadedProjectsAsync()).ReturnsAsync(projects);

        var args = new Microsoft.Extensions.AI.AIFunctionArguments
        {
            { "includeFiles", false }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("ProjA.csproj", result);
        Assert.Contains("ProjC.csproj", result);
        Assert.DoesNotContain("### Source Files", result);
    }

    [Fact]
    public async Task GetWorkspaceInfoAsync_IncludeFilesTrue_CapsAt100Files()
    {
        // A-WI-002
        var sut = CreateSut();
        
        var files = Enumerable.Range(1, 105).Select(i => $"file{i}.cs").ToList();
        CreateTempFiles(files);

        var args = new Microsoft.Extensions.AI.AIFunctionArguments
        {
            { "includeFiles", true }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("### Source Files", result);
        Assert.Contains("C# Files:** 105", result);
        Assert.Contains("... and 5 more files", result);
    }

    [Fact]
    public async Task GetWorkspaceInfoAsync_NoActiveWorkspace_ReturnsGracefulError()
    {
        // A-WI-003
        var sut = CreateSut();
        
        _mockVsEnv.Setup(v => v.GetWorkspaceRootAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync((string?)null); // No active workspace

        var args = new Microsoft.Extensions.AI.AIFunctionArguments
        {
            { "includeFiles", false }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("No solution is currently loaded", result);
    }

    [Fact]
    public async Task GetWorkspaceInfoAsync_ManyProjects_CapsAt20()
    {
        // A-WI-004
        var sut = CreateSut();
        
        var projects = Enumerable.Range(1, 30).Select(i => $"Project{i}.csproj").ToList();
        _mockVsEnv.Setup(v => v.GetLoadedProjectsAsync()).ReturnsAsync(projects);

        var args = new Microsoft.Extensions.AI.AIFunctionArguments
        {
            { "includeFiles", false }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("Projects:** 30", result);
        Assert.Contains("... and 10 more projects", result);
    }

    [Fact]
    public async Task GetWorkspaceInfoAsync_ObjBinDirectories_ExcludedFromFileScan()
    {
        // A-WI-005
        var sut = CreateSut();
        
        var files = new List<string> 
        { 
            "src/ValidFile1.cs",
            "src/ValidFile2.cs",
            "bin/Debug/InvalidFile1.cs",
            "obj/Debug/InvalidFile2.cs"
        };
        CreateTempFiles(files);

        var args = new Microsoft.Extensions.AI.AIFunctionArguments
        {
            { "includeFiles", true }
        };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("C# Files:** 2", result); // Only the 2 valid ones
        Assert.Contains("ValidFile1.cs", result);
        Assert.DoesNotContain("InvalidFile1.cs", result);
        Assert.DoesNotContain("InvalidFile2.cs", result);
    }
}

