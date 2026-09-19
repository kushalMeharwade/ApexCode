using System.Linq;
using AiAssistant.Engine.Services;
using NUnit.Framework;

namespace AiAssistant.Tests.Engine;

[TestFixture]
public class DependencyGraphTests
{
    [Test]
    public void AddExplicitDependency_AddsNode()
    {
        // Arrange
        var graph = new DependencyGraph();

        // Act
        graph.AddExplicitDependency("source.cs", "target.cs", "Test", 1000);

        // Assert
        Assert.AreEqual(1, graph.FileCount);
        Assert.AreEqual(1000, graph.TotalTokenCount);
    }

    [Test]
    public void IsVisited_CycleDetection_Works()
    {
        // Arrange
        var graph = new DependencyGraph();

        // Act
        graph.MarkVisited("file.cs");

        // Assert
        Assert.IsTrue(graph.IsVisited("file.cs"));
        Assert.IsFalse(graph.IsVisited("other.cs"));
    }

    [Test]
    public void IsVisited_CaseInsensitive()
    {
        // Arrange
        var graph = new DependencyGraph();

        // Act
        graph.MarkVisited("File.cs");

        // Assert
        Assert.IsTrue(graph.IsVisited("file.CS"));
        Assert.IsTrue(graph.IsVisited("FILE.cs"));
    }

    [Test]
    public void GetResolvedFiles_RespectsMaxFiles()
    {
        // Arrange
        var graph = new DependencyGraph();
        for (int i = 0; i < 30; i++)
        {
            graph.AddInferredDependency($"file{i}.cs", "Test", 0.8f, 2, 1000, 0);
        }

        var policy = new ResolutionPolicy { MaxFiles = 10, MaxTokens = 1000000 };

        // Act
        var results = graph.GetResolvedFiles(policy);

        // Assert
        Assert.LessOrEqual(results.Count, 10);
    }

    [Test]
    public void GetResolvedFiles_RespectsMaxTokens()
    {
        // Arrange
        var graph = new DependencyGraph();
        for (int i = 0; i < 30; i++)
        {
            graph.AddInferredDependency($"file{i}.cs", "Test", 0.8f, 2, 5000, 0);
        }

        var policy = new ResolutionPolicy { MaxFiles = 100, MaxTokens = 20000 };

        // Act
        var results = graph.GetResolvedFiles(policy);
        var totalTokens = results.Sum(r => r.EstimatedTokens);

        // Assert
        Assert.LessOrEqual(totalTokens, 20000);
    }

    [Test]
    public void GetResolvedFiles_RespectsMinConfidence()
    {
        // Arrange
        var graph = new DependencyGraph();
        graph.AddInferredDependency("high.cs", "High confidence", 0.9f, 1, 1000, 0);
        graph.AddInferredDependency("low.cs", "Low confidence", 0.5f, 2, 1000, 0);

        var policy = new ResolutionPolicy { MinConfidence = 0.7f, MaxFiles = 100, MaxTokens = 100000 };

        // Act
        var results = graph.GetResolvedFiles(policy);

        // Assert
        Assert.AreEqual(1, results.Count);
        Assert.IsTrue(results[0].FilePath.Contains("high"));
    }

    [Test]
    public void GetResolvedFiles_PrioritizesByTier()
    {
        // Arrange
        var graph = new DependencyGraph();
        graph.AddInferredDependency("tier2.cs", "Tier 2", 0.9f, 2, 1000, 0);
        graph.AddExplicitDependency("source.cs", "tier1.cs", "Tier 1", 1000);

        var policy = new ResolutionPolicy { MaxFiles = 1, MaxTokens = 100000 };

        // Act
        var results = graph.GetResolvedFiles(policy);

        // Assert
        Assert.AreEqual(1, results.Count);
        Assert.IsTrue(results[0].FilePath.Contains("tier1")); // Tier 1 should win
    }

    [Test]
    public void GetResolvedFiles_AppliesSkeletonStrategy()
    {
        // Arrange
        var graph = new DependencyGraph();
        graph.AddInferredDependency("large.cs", "Large file", 0.9f, 1, 50000, 0); // 50k tokens

        var policy = new ResolutionPolicy
        {
            MaxFiles = 10,
            MaxTokens = 20000,
            LargeFileStrategy = LargeFileStrategy.Skeleton,
            LargeFileThreshold = 1000
        };

        // Act
        var results = graph.GetResolvedFiles(policy);

        // Assert
        Assert.AreEqual(1, results.Count);
        Assert.IsTrue(results[0].IsPartial);
        Assert.AreEqual("Skeleton", results[0].ProcessingStrategy);
        Assert.Less(results[0].EstimatedTokens, 50000); // Should be reduced
    }

    [Test]
    public void GetResolvedFiles_ExcludesLargeFilesWhenStrategyIsExclude()
    {
        // Arrange
        var graph = new DependencyGraph();
        graph.AddInferredDependency("small.cs", "Small", 0.9f, 1, 1000, 0);
        graph.AddInferredDependency("large.cs", "Large", 0.9f, 1, 50000, 0);

        var policy = new ResolutionPolicy
        {
            MaxFiles = 10,
            MaxTokens = 10000,
            LargeFileStrategy = LargeFileStrategy.Exclude,
            LargeFileThreshold = 1000
        };

        // Act
        var results = graph.GetResolvedFiles(policy);

        // Assert
        Assert.AreEqual(1, results.Count);
        Assert.IsTrue(results[0].FilePath.Contains("small"));
    }

    [Test]
    public void AddInferredDependency_UpgradesConfidence()
    {
        // Arrange
        var graph = new DependencyGraph();

        // Act
        graph.AddInferredDependency("file.cs", "Low", 0.5f, 3, 1000, 0);
        graph.AddInferredDependency("file.cs", "High", 0.9f, 2, 1000, 0);

        // Assert
        var policy = new ResolutionPolicy { MinConfidence = 0.7f, MaxFiles = 10, MaxTokens = 100000 };
        var results = graph.GetResolvedFiles(policy);
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(0.9f, results[0].Confidence);
    }

    [Test]
    public void Clear_ResetsGraph()
    {
        // Arrange
        var graph = new DependencyGraph();
        graph.AddExplicitDependency("source.cs", "target.cs", "Test", 1000);
        graph.MarkVisited("file.cs");

        // Act
        graph.Clear();

        // Assert
        Assert.AreEqual(0, graph.FileCount);
        Assert.AreEqual(0, graph.TotalTokenCount);
        Assert.IsFalse(graph.IsVisited("file.cs"));
    }
}
