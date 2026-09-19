using System;
using System.Collections.Generic;
using System.Linq;

namespace AiAssistant.Engine.Services;

/// <summary>
/// Manages a graph of file dependencies with cycle detection and token budget tracking.
/// Thread-safe for concurrent resolution operations.
/// </summary>
public class DependencyGraph
{
    private readonly Dictionary<string, DependencyNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _visitedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private int _totalTokenCount = 0;

    /// <summary>
    /// Adds an explicit dependency (Tier 1, confidence = 1.0).
    /// </summary>
    public void AddExplicitDependency(string sourceFile, string targetFile, string reason, int estimatedTokens)
    {
        lock (_lock)
        {
            if (!_nodes.TryGetValue(targetFile, out var node))
            {
                node = new DependencyNode
                {
                    FilePath = targetFile,
                    Confidence = 1.0f,
                    Reason = reason,
                    Tier = 1,
                    EstimatedTokens = estimatedTokens,
                    Depth = CalculateDepth(sourceFile) + 1
                };
                _nodes[targetFile] = node;
                _totalTokenCount += estimatedTokens;
            }
        }
    }

    /// <summary>
    /// Adds an inferred dependency (Tier 2+, confidence < 1.0).
    /// </summary>
    public void AddInferredDependency(string targetFile, string reason, float confidence, int tier, int estimatedTokens, int depth = 0)
    {
        lock (_lock)
        {
            if (!_nodes.TryGetValue(targetFile, out var node))
            {
                node = new DependencyNode
                {
                    FilePath = targetFile,
                    Confidence = confidence,
                    Reason = reason,
                    Tier = tier,
                    EstimatedTokens = estimatedTokens,
                    Depth = depth
                };
                _nodes[targetFile] = node;
                _totalTokenCount += estimatedTokens;
            }
            else
            {
                // If already exists, upgrade confidence if higher
                if (confidence > node.Confidence)
                {
                    node.Confidence = confidence;
                    node.Reason = reason;
                    node.Tier = Math.Min(tier, node.Tier);
                }
            }
        }
    }

    /// <summary>
    /// Checks if a file has already been visited (cycle detection).
    /// </summary>
    public bool IsVisited(string filePath)
    {
        lock (_lock)
        {
            return _visitedFiles.Contains(filePath);
        }
    }

    /// <summary>
    /// Marks a file as visited.
    /// </summary>
    public void MarkVisited(string filePath)
    {
        lock (_lock)
        {
            _visitedFiles.Add(filePath);
        }
    }

    /// <summary>
    /// Gets resolved files with filtering and prioritization.
    /// </summary>
    public List<DependencyNode> GetResolvedFiles(ResolutionPolicy policy)
    {
        lock (_lock)
        {
            var results = new List<DependencyNode>();
            int accumulatedTokens = 0;

            // Filter by confidence threshold
            var candidates = _nodes.Values
                .Where(n => n.Confidence >= policy.MinConfidence)
                .OrderBy(n => n.Tier)                    // Tier 1 first
                .ThenByDescending(n => n.Confidence)     // Higher confidence
                .ThenBy(n => n.Depth)                    // Closer to root
                .ThenBy(n => n.EstimatedTokens)          // Smaller files
                .ToList();

            foreach (var node in candidates)
            {
                // Check file count limit
                if (results.Count >= policy.MaxFiles)
                    break;

                // Check token budget
                if (accumulatedTokens + node.EstimatedTokens > policy.MaxTokens)
                {
                    // Try to apply large file strategy
                    if (node.EstimatedTokens > policy.LargeFileThreshold * 4) // 4 tokens/line estimate
                    {
                        switch (policy.LargeFileStrategy)
                        {
                            case LargeFileStrategy.Skeleton:
                                // Mark for skeleton extraction
                                node.IsPartial = true;
                                node.ProcessingStrategy = "Skeleton";
                                // Estimate skeleton is ~20% of original
                                node.EstimatedTokens = (int)(node.EstimatedTokens * 0.2);
                                break;

                            case LargeFileStrategy.Truncate:
                                node.IsPartial = true;
                                node.ProcessingStrategy = "Truncate";
                                node.EstimatedTokens = policy.LargeFileThreshold * 4;
                                break;

                            case LargeFileStrategy.Exclude:
                                continue; // Skip this file entirely
                        }
                    }
                    else
                    {
                        break; // Budget exhausted, can't fit even with strategy
                    }
                }

                results.Add(node);
                accumulatedTokens += node.EstimatedTokens;
            }

            return results;
        }
    }

    /// <summary>
    /// Gets total estimated token count for all nodes.
    /// </summary>
    public int TotalTokenCount => _totalTokenCount;

    /// <summary>
    /// Gets count of unique files in graph.
    /// </summary>
    public int FileCount
    {
        get
        {
            lock (_lock)
            {
                return _nodes.Count;
            }
        }
    }

    /// <summary>
    /// Clears the graph (for new resolution).
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _nodes.Clear();
            _visitedFiles.Clear();
            _totalTokenCount = 0;
        }
    }

    private int CalculateDepth(string filePath)
    {
        if (_nodes.TryGetValue(filePath, out var node))
            return node.Depth;
        return 0;
    }
}

/// <summary>
/// Represents a node in the dependency graph.
/// </summary>
public class DependencyNode
{
    public string FilePath { get; set; } = "";
    public float Confidence { get; set; }
    public string Reason { get; set; } = "";
    public int Tier { get; set; }
    public int EstimatedTokens { get; set; }
    public int Depth { get; set; }
    public bool IsPartial { get; set; }
    public string? ProcessingStrategy { get; set; }
}

/// <summary>
/// Policy for dependency resolution.
/// </summary>
public class ResolutionPolicy
{
    public int MaxDepth { get; set; } = 2;
    public float MinConfidence { get; set; } = 0.7f;
    public int MaxFiles { get; set; } = 20;
    public int MaxTokens { get; set; } = 50000;
    public TokenEstimationStrategy TokenEstimationStrategy { get; set; } = TokenEstimationStrategy.CharacterCount;
    public LargeFileStrategy LargeFileStrategy { get; set; } = LargeFileStrategy.Skeleton;
    public int LargeFileThreshold { get; set; } = 1000; // lines
    public bool RespectTechnologyBoundaries { get; set; } = true;
    public bool RespectProjectBoundaries { get; set; } = true;
    public bool EnableHeuristics { get; set; } = false;
}

public enum TokenEstimationStrategy
{
    LineCount,      // Fast: lineCount × 4
    CharacterCount, // Balanced: charCount / 4
    ActualTokenize  // Accurate: use tiktoken (slow)
}

public enum LargeFileStrategy
{
    Exclude,   // Skip files > threshold
    Skeleton,  // Extract signatures only
    Truncate   // Include first N lines
}
