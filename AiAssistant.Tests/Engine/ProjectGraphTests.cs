using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AiAssistant.Engine.Services;
using NUnit.Framework;

namespace AiAssistant.Tests.Engine;

[TestFixture]
public class ProjectGraphTests
{
    private string _testDataDir = "";

    [SetUp]
    public void Setup()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "ProjectGraphTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDataDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_testDataDir))
        {
            Directory.Delete(_testDataDir, recursive: true);
        }
    }

    [Test]
    public async Task LoadFromSolution_ParsesProjects()
    {
        // Arrange
        var solutionPath = CreateTestSolution();
        var graph = new ProjectGraph();

        // Act
        await graph.LoadFromSolutionAsync(solutionPath);

        // Assert
        var projectA = graph.GetProject(Path.Combine(_testDataDir, "ProjectA", "Class1.cs"));
        Assert.IsNotNull(projectA);
        Assert.AreEqual("ProjectA", Path.GetFileNameWithoutExtension(projectA!.ProjectPath));
    }

    [Test]
    public void CanReference_SameProject_ReturnsTrue()
    {
        // Arrange
        var graph = new ProjectGraph();
        var project = new ProjectInfo
        {
            ProjectPath = Path.Combine(_testDataDir, "ProjectA.csproj"),
            ProjectReferences = new()
        };

        // Act
        var canReference = graph.CanReference(project, project);

        // Assert
        Assert.IsTrue(canReference);
    }

    [Test]
    public void CanReference_WithProjectReference_ReturnsTrue()
    {
        // Arrange
        var graph = new ProjectGraph();
        var projectBPath = Path.Combine(_testDataDir, "ProjectB.csproj");
        var projectA = new ProjectInfo
        {
            ProjectPath = Path.Combine(_testDataDir, "ProjectA.csproj"),
            ProjectReferences = new() { projectBPath }
        };
        var projectB = new ProjectInfo
        {
            ProjectPath = projectBPath,
            ProjectReferences = new()
        };

        // Act
        var canReference = graph.CanReference(projectA, projectB);

        // Assert
        Assert.IsTrue(canReference);
    }

    [Test]
    public void CanReference_NoProjectReference_ReturnsFalse()
    {
        // Arrange
        var graph = new ProjectGraph();
        var projectA = new ProjectInfo
        {
            ProjectPath = Path.Combine(_testDataDir, "ProjectA.csproj"),
            ProjectReferences = new()
        };
        var projectB = new ProjectInfo
        {
            ProjectPath = Path.Combine(_testDataDir, "ProjectB.csproj"),
            ProjectReferences = new()
        };

        // Act
        var canReference = graph.CanReference(projectA, projectB);

        // Assert
        Assert.IsFalse(canReference);
    }

    [Test]
    public async Task LoadFromSolution_IndexesFiles()
    {
        // Arrange
        var solutionPath = CreateTestSolution();
        var graph = new ProjectGraph();

        // Act
        await graph.LoadFromSolutionAsync(solutionPath);

        // Assert
        var class1Path = Path.Combine(_testDataDir, "ProjectA", "Class1.cs");
        var project = graph.GetProject(class1Path);
        Assert.IsNotNull(project);
        Assert.IsTrue(project!.Files.Contains(class1Path));
    }

    // Helper methods to create test files
    private string CreateTestSolution()
    {
        var solutionPath = Path.Combine(_testDataDir, "TestSolution.sln");
        var solutionContent = @"
Microsoft Visual Studio Solution File, Format Version 12.00
Project(""{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"") = ""ProjectA"", ""ProjectA\ProjectA.csproj"", ""{11111111-1111-1111-1111-111111111111}""
EndProject
Project(""{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"") = ""ProjectB"", ""ProjectB\ProjectB.csproj"", ""{22222222-2222-2222-2222-222222222222}""
EndProject
";
        File.WriteAllText(solutionPath, solutionContent);

        // Create ProjectA
        var projectADir = Path.Combine(_testDataDir, "ProjectA");
        Directory.CreateDirectory(projectADir);
        var projectAPath = Path.Combine(projectADir, "ProjectA.csproj");
        var projectAContent = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>ProjectA</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include=""..\ProjectB\ProjectB.csproj"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(projectAPath, projectAContent);
        File.WriteAllText(Path.Combine(projectADir, "Class1.cs"), "namespace ProjectA { public class Class1 { } }");

        // Create ProjectB
        var projectBDir = Path.Combine(_testDataDir, "ProjectB");
        Directory.CreateDirectory(projectBDir);
        var projectBPath = Path.Combine(projectBDir, "ProjectB.csproj");
        var projectBContent = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>ProjectB</RootNamespace>
  </PropertyGroup>
</Project>";
        File.WriteAllText(projectBPath, projectBContent);
        File.WriteAllText(Path.Combine(projectBDir, "Class2.cs"), "namespace ProjectB { public class Class2 { } }");

        return solutionPath;
    }
}
