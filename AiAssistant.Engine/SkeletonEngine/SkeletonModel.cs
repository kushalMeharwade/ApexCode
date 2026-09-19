using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AiAssistant.Engine.SkeletonEngine;

public static class ContentHash
{
    public static string Compute(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(bytes);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}

public enum SymbolKind
{
    Using,
    Namespace,
    Class,
    Struct,
    Interface,
    Enum,
    Delegate,
    Method,
    Property,
    Field,
    Constructor,
    Event,
    Unknown
}

public sealed class SymbolNode
{
    public SymbolKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Signature { get; set; } = "";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public int BodyTokenEstimate { get; set; }
    public List<SymbolNode> Children { get; set; } = new();

    public SymbolNode() { }
    public SymbolNode(SymbolKind kind, string name, string signature, int startLine, int endLine, int bodyTokenEstimate = 0)
    {
        Kind = kind;
        Name = name;
        Signature = signature;
        StartLine = startLine;
        EndLine = endLine;
        BodyTokenEstimate = bodyTokenEstimate;
    }
}

public enum ContextStatus
{
    Full,
    Skeleton,
    SkeletonTruncated,
    Excerpt,
    Omitted
}

public sealed class SkeletonResult
{
    public string Path { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string Outline { get; set; } = "";
    public List<SymbolNode> Symbols { get; set; } = new();
    
    public ContextStatus StatusKind { get; set; } = ContextStatus.Omitted;
    public string StatusDetail { get; set; } = "OMITTED";
    public int IncludedLines { get; set; } = 0;
    public int TotalLines { get; set; } = 0;

    public SkeletonResult CloneWith(string path) => new()
    {
        Path = path,
        ContentHash = ContentHash,
        Outline = Outline,
        Symbols = Symbols,
        StatusKind = StatusKind,
        StatusDetail = StatusDetail,
        IncludedLines = IncludedLines,
        TotalLines = TotalLines
    };
}

public interface ISkeletonParser
{
    SkeletonResult Parse(string source, string extension, bool ignoreLimits = false);
    bool Supports(string extension);
}
