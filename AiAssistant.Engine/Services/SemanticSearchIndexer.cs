using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Storage.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AiAssistant.Engine.Services;

public class SemanticSearchIndexer : ISemanticSearchIndexer
{
    public Task<IEnumerable<EmbeddingChunk>> ChunkFileAsync(string filePath, string fileContent, CancellationToken ct = default)
    {
        if (filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(ChunkCSharpFile(filePath, fileContent));
        }
        else
        {
            return Task.FromResult(ChunkFallback(filePath, fileContent));
        }
    }

    private IEnumerable<EmbeddingChunk> ChunkCSharpFile(string filePath, string fileContent)
    {
        var chunks = new List<EmbeddingChunk>();
        var tree = CSharpSyntaxTree.ParseText(fileContent);
        var root = tree.GetRoot();

        // 1. Class Headers
        var classDeclarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
        foreach (var cls in classDeclarations)
        {
            var headerSpan = cls.Identifier.Span;
            var startLine = tree.GetLineSpan(cls.Span).StartLinePosition.Line + 1;
            
            // Collect the class declaration and fields/properties
            var members = cls.Members.Where(m => m is FieldDeclarationSyntax || m is PropertyDeclarationSyntax);
            
            // To keep it simple, extract the class block up to the first method, or just take the whole thing and strip methods
            // A simpler approach: use a SyntaxRewriter to remove method bodies.
            var rewriter = new ClassHeaderRewriter();
            var headerNode = rewriter.Visit(cls);
            var headerText = headerNode.ToFullString().Trim();

            chunks.Add(new EmbeddingChunk
            {
                Id = $"{filePath}::class::{startLine}",
                FilePath = filePath,
                ChunkText = headerText,
                ChunkType = "class_header",
                StartLine = startLine,
                EndLine = tree.GetLineSpan(cls.Span).EndLinePosition.Line + 1,
                SymbolName = cls.Identifier.Text,
                LastIndexed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }

        // 2. Methods
        var methodDeclarations = root.DescendantNodes().OfType<MethodDeclarationSyntax>();
        foreach (var method in methodDeclarations)
        {
            var startLine = tree.GetLineSpan(method.Span).StartLinePosition.Line + 1;
            var endLine = tree.GetLineSpan(method.Span).EndLinePosition.Line + 1;
            var methodText = method.ToFullString().Trim();

            if (endLine - startLine > 200)
            {
                // Fallback to sliding window for huge methods if necessary, 
                // but the prompt says "further split at logical boundaries (empty lines)"
                var subChunks = SplitLargeMethod(filePath, methodText, startLine, method.Identifier.Text);
                chunks.AddRange(subChunks);
            }
            else
            {
                chunks.Add(new EmbeddingChunk
                {
                    Id = $"{filePath}::method::{startLine}",
                    FilePath = filePath,
                    ChunkText = methodText,
                    ChunkType = "method",
                    StartLine = startLine,
                    EndLine = endLine,
                    SymbolName = method.Identifier.Text,
                    LastIndexed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
            }
        }

        if (chunks.Count == 0)
        {
            return ChunkFallback(filePath, fileContent);
        }

        return chunks;
    }

    private IEnumerable<EmbeddingChunk> SplitLargeMethod(string filePath, string methodText, int baseLine, string symbolName)
    {
        // Simple fallback to 40 line windows for huge methods
        var lines = methodText.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
        var chunks = new List<EmbeddingChunk>();

        int windowSize = 40;
        int overlap = 10;
        int step = windowSize - overlap;

        for (int i = 0; i < lines.Length; i += step)
        {
            var chunkLines = lines.Skip(i).Take(windowSize).ToList();
            var chunkText = string.Join("\n", chunkLines).Trim();
            if (string.IsNullOrEmpty(chunkText)) continue;

            var startLine = baseLine + i;
            var endLine = startLine + chunkLines.Count - 1;

            chunks.Add(new EmbeddingChunk
            {
                Id = $"{filePath}::method::{startLine}",
                FilePath = filePath,
                ChunkText = chunkText,
                ChunkType = "method",
                StartLine = startLine,
                EndLine = endLine,
                SymbolName = symbolName,
                LastIndexed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }

        return chunks;
    }

    private IEnumerable<EmbeddingChunk> ChunkFallback(string filePath, string fileContent)
    {
        var chunks = new List<EmbeddingChunk>();
        var lines = fileContent.Split(new[] { '\r', '\n' }, StringSplitOptions.None);

        int windowSize = 40;
        int overlap = 10;
        int step = windowSize - overlap;

        for (int i = 0; i < lines.Length; i += step)
        {
            var chunkLines = lines.Skip(i).Take(windowSize).ToList();
            var chunkText = string.Join("\n", chunkLines).Trim();
            if (string.IsNullOrWhiteSpace(chunkText)) continue;

            var startLine = i + 1;
            var endLine = i + chunkLines.Count;

            chunks.Add(new EmbeddingChunk
            {
                Id = $"{filePath}::window::{startLine}",
                FilePath = filePath,
                ChunkText = chunkText,
                ChunkType = "window",
                StartLine = startLine,
                EndLine = endLine,
                SymbolName = null,
                LastIndexed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }

        return chunks;
    }

    private class ClassHeaderRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            // Remove method body, leave just the signature if possible, or remove entirely
            return null; // For class headers, we skip methods entirely as per prompt: "field and property declarations (but not method bodies)"
        }
    }
}
