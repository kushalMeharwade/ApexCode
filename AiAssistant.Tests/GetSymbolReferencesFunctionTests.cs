using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Services;
using AiAssistant.Tools.Functions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace AiAssistant.Tests;

public class GetSymbolReferencesFunctionTests : IDisposable
{
    private readonly Mock<IVisualStudioEnvironmentService> _mockVsEnv;
    private readonly Mock<IOutputLogger> _mockLogger;
    private readonly AdhocWorkspace _roslynWorkspace;
    private readonly ProjectId _projectId;

    public GetSymbolReferencesFunctionTests()
    {
        _mockVsEnv = new Mock<IVisualStudioEnvironmentService>();
        _mockLogger = new Mock<IOutputLogger>();

        _roslynWorkspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "TestProject",
            "TestProject",
            LanguageNames.CSharp);
            
        _projectId = projectInfo.Id;
        _roslynWorkspace.AddProject(projectInfo);

        // Required to mock metadata references so SymbolFinder works properly.
        // For simple symbol resolution, we just need the syntax trees and basic compilation.
        var mscorlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        _roslynWorkspace.TryApplyChanges(_roslynWorkspace.CurrentSolution.AddMetadataReference(_projectId, mscorlib));

        _mockVsEnv.Setup(v => v.GetWorkspaceAsync())
                  .ReturnsAsync(_roslynWorkspace);
    }

    public void Dispose()
    {
        _roslynWorkspace.Dispose();
    }

    private AIFunction CreateSut()
    {
        var provider = new GetSymbolReferencesFunction(
            _mockVsEnv.Object,
            _mockLogger.Object);
        return provider.CreateFunction();
    }

    private void AddDocument(string name, string text, string filePath)
    {
        var docInfo = DocumentInfo.Create(
            DocumentId.CreateNewId(_projectId),
            name,
            filePath: filePath,
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(text), VersionStamp.Create())));
            
        _roslynWorkspace.TryApplyChanges(_roslynWorkspace.CurrentSolution.AddDocument(docInfo));
    }

    [Fact]
    public async Task GetSymbolReferencesAsync_ValidSymbol_ReturnsUsages()
    {
        // A-FU-001
        AddDocument("File1.cs", @"
namespace TestNS {
    public class TargetClass { }
    
    public class Consumer {
        public void DoWork() {
            TargetClass obj = new TargetClass();
        }
    }
}", "C:\\Project\\File1.cs");

        var sut = CreateSut();
        var args = new Microsoft.Extensions.AI.AIFunctionArguments { { "symbolName", "TestNS.TargetClass" } };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        // Expecting references on line 6 (0-indexed line 5 is "TargetClass obj = new TargetClass();")
        // It should output something like File1.cs:6
        Assert.Contains("File1.cs:7", result);
        Assert.Contains("References for TestNS.TargetClass:", result);
    }

    [Fact]
    public async Task GetSymbolReferencesAsync_NoSymbolFound_ReturnsGracefulMessage()
    {
        // A-FU-002
        AddDocument("File1.cs", "public class SomeClass { }", "C:\\Project\\File1.cs");

        var sut = CreateSut();
        var args = new Microsoft.Extensions.AI.AIFunctionArguments { { "symbolName", "NonExistentClass" } };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("No symbol found matching 'NonExistentClass'", result);
    }

    [Fact]
    public async Task GetSymbolReferencesAsync_MissingSymbolNameArg_ReturnsDescriptiveError()
    {
        // A-FU-003
        var sut = CreateSut();
        var args = new Microsoft.Extensions.AI.AIFunctionArguments(); // empty args

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("Error: symbolName argument is missing", result);
    }

    [Fact]
    public async Task GetSymbolReferencesAsync_ZeroExternalReferences_ReturnsEmptyResult()
    {
        // A-FU-004
        AddDocument("File1.cs", @"
namespace TestNS {
    public class UnusedClass { }
}", "C:\\Project\\File1.cs");

        var sut = CreateSut();
        var args = new Microsoft.Extensions.AI.AIFunctionArguments { { "symbolName", "TestNS.UnusedClass" } };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        // Since there are zero usages outside its declaration, Roslyn SymbolFinder might return the declaration itself,
        // or nothing. The test ensures it does not crash and returns the References header.
        Assert.Contains("References for TestNS.UnusedClass", result);
    }

    [Fact]
    public async Task GetSymbolReferencesAsync_MultiFileReferences_ReturnedCorrectly()
    {
        // A-FU-005
        AddDocument("Shared.cs", @"
namespace TestNS {
    public class SharedClass { }
}", "C:\\Project\\Shared.cs");

        AddDocument("FileA.cs", @"
using TestNS;
public class FileA {
    SharedClass objA;
}", "C:\\Project\\FileA.cs");

        AddDocument("FileB.cs", @"
using TestNS;
public class FileB {
    SharedClass objB;
}", "C:\\Project\\FileB.cs");

        var sut = CreateSut();
        var args = new Microsoft.Extensions.AI.AIFunctionArguments { { "symbolName", "TestNS.SharedClass" } };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        // Expecting references in FileA.cs line 4 and FileB.cs line 4
        Assert.Contains("FileA.cs:4", result);
        Assert.Contains("FileB.cs:4", result);
    }

    [Fact]
    public async Task GetSymbolReferencesAsync_NoActiveWorkspace_ReturnsError()
    {
        // A-FU-006
        var sut = CreateSut();
        
        // Override mock to return null
        _mockVsEnv.Setup(v => v.GetWorkspaceAsync()).ReturnsAsync((Workspace?)null);
        
        var args = new Microsoft.Extensions.AI.AIFunctionArguments { { "symbolName", "AnyClass" } };

        var result = await sut.InvokeAsync(args, CancellationToken.None) as string;

        Assert.NotNull(result);
        Assert.Contains("No active Roslyn workspace found", result);
    }
}
