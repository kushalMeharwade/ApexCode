using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AiAssistant.Engine.SkeletonEngine;

public sealed class CSharpParser : ISkeletonParser
{
    private const int TokenCap = 1500;
    private const int MaxSymbols = 200;

    public bool Supports(string extension) =>
        string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase);

    public SkeletonResult Parse(string source, string extension, bool ignoreLimits = false)
    {
        var hash = ContentHash.Compute(source);
        SkeletonResult result;
        try
        {
            var tree = CSharpSyntaxTree.ParseText(source);
            var root = tree.GetRoot();
            var symbols = new List<SymbolNode>();
            var sb = new StringBuilder();
            bool isTruncatedByParser = false;

            var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>().ToList();
            foreach (var u in usings)
            {
                var line = tree.GetLineSpan(u.Span).StartLinePosition.Line + 1;
                symbols.Add(new SymbolNode(SymbolKind.Using, u.Name.ToString(), u.ToString().Trim(), line, line));
                // Usings are intentionally omitted from the string builder output
            }

            var namespaceDecls = root.DescendantNodes().OfType<NamespaceDeclarationSyntax>().ToList();
            foreach (var ns in namespaceDecls)
            {
                var startLine = tree.GetLineSpan(ns.Span).StartLinePosition.Line + 1;
                var endLine = tree.GetLineSpan(ns.Span).EndLinePosition.Line + 1;
                symbols.Add(new SymbolNode(SymbolKind.Namespace, ns.Name.ToString(), ns.Name.ToString(), startLine, endLine));
                sb.AppendLine($"namespace {ns.Name}");
            }

            var typeDecls = root.DescendantNodes()
                .Where(n => n is ClassDeclarationSyntax || n is StructDeclarationSyntax || n is InterfaceDeclarationSyntax || n is EnumDeclarationSyntax || n is DelegateDeclarationSyntax)
                .ToList();

            foreach (var type in typeDecls)
            {
                var span = type switch
                {
                    ClassDeclarationSyntax c => c.Span,
                    StructDeclarationSyntax s => s.Span,
                    InterfaceDeclarationSyntax i => i.Span,
                    EnumDeclarationSyntax e => e.Span,
                    DelegateDeclarationSyntax d => d.Span,
                    _ => default
                };
                if (span == default) continue;

                var startLine = tree.GetLineSpan(span).StartLinePosition.Line + 1;
                var endLine = tree.GetLineSpan(span).EndLinePosition.Line + 1;
                var kind = type switch
                {
                    ClassDeclarationSyntax => SymbolKind.Class,
                    StructDeclarationSyntax => SymbolKind.Struct,
                    InterfaceDeclarationSyntax => SymbolKind.Interface,
                    EnumDeclarationSyntax => SymbolKind.Enum,
                    DelegateDeclarationSyntax => SymbolKind.Delegate,
                    _ => SymbolKind.Unknown
                };

                var name = type switch
                {
                    ClassDeclarationSyntax c => c.Identifier.Text,
                    StructDeclarationSyntax s => s.Identifier.Text,
                    InterfaceDeclarationSyntax i => i.Identifier.Text,
                    EnumDeclarationSyntax e => e.Identifier.Text,
                    DelegateDeclarationSyntax d => d.Identifier.Text,
                    _ => "?"
                };

                var header = BuildTypeHeader(type, tree);
                var bodyTokens = EstimateTokenCount(type);
                var node = new SymbolNode(kind, name, header, startLine, endLine, bodyTokens);
                symbols.Add(node);

                var fields = new List<string>();
                var fieldNames = new List<string>();
                var properties = new List<string>();
                var constructors = new List<string>();
                var methods = new List<string>();
                var events = new List<string>();
                var others = new List<string>();

                foreach (var member in GetTypeMembers(type))
                {
                    var memberStart = tree.GetLineSpan(member.Span).StartLinePosition.Line + 1;
                    var memberEnd = tree.GetLineSpan(member.Span).EndLinePosition.Line + 1;
                    var memberKind = member switch
                    {
                        MethodDeclarationSyntax => SymbolKind.Method,
                        PropertyDeclarationSyntax => SymbolKind.Property,
                        FieldDeclarationSyntax => SymbolKind.Field,
                        ConstructorDeclarationSyntax => SymbolKind.Constructor,
                        EventDeclarationSyntax => SymbolKind.Event,
                        ClassDeclarationSyntax => SymbolKind.Class,
                        StructDeclarationSyntax => SymbolKind.Struct,
                        InterfaceDeclarationSyntax => SymbolKind.Interface,
                        EnumDeclarationSyntax => SymbolKind.Enum,
                        DelegateDeclarationSyntax => SymbolKind.Delegate,
                        IndexerDeclarationSyntax => SymbolKind.Property,
                        _ => SymbolKind.Unknown
                    };
                    var memberName = GetMemberName(member);
                    var memberSignature = GetMemberSignature(member).Trim();
                    var child = new SymbolNode(memberKind, memberName, memberSignature, memberStart, memberEnd);
                    node.Children.Add(child);
                    
                    var signatureWithLines = memberStart == memberEnd 
                        ? $"{memberSignature} [L{memberStart}]" 
                        : $"{memberSignature} [L{memberStart}-L{memberEnd}]";
                    
                    switch (memberKind)
                    {
                        case SymbolKind.Field: 
                            fields.Add(signatureWithLines); 
                            fieldNames.Add(memberName);
                            break;
                        case SymbolKind.Property: properties.Add(signatureWithLines); break;
                        case SymbolKind.Constructor: constructors.Add(signatureWithLines); break;
                        case SymbolKind.Method: methods.Add(signatureWithLines); break;
                        case SymbolKind.Event: events.Add(signatureWithLines); break;
                        default: others.Add(signatureWithLines); break;
                    }
                }

                var totalMethods = methods.Count;
                var methodTruncated = false;
                if (!ignoreLimits && methods.Count > 50)
                {
                    methods = methods.Take(50).ToList();
                    methodTruncated = true;
                    isTruncatedByParser = true;
                }

                var kindName = kind switch {
                    SymbolKind.Class => "Class",
                    SymbolKind.Struct => "Struct",
                    SymbolKind.Interface => "Interface",
                    SymbolKind.Enum => "Enum",
                    SymbolKind.Delegate => "Delegate",
                    _ => "Type"
                };

                var summary = $"{kindName} {name} — {fields.Count} fields, {properties.Count} properties, {constructors.Count} constructors, {totalMethods} methods";
                if (methodTruncated)
                {
                    summary += " (outline truncated; showing first 50 methods).";
                }
                else
                {
                    summary += ".";
                }

                sb.AppendLine(header);
                sb.AppendLine($"  {summary}");

                if (methods.Count > 0) sb.AppendLine($"  Methods: {string.Join(", ", methods)}");
                if (constructors.Count > 0) sb.AppendLine($"  Constructors: {string.Join(", ", constructors)}");
                if (properties.Count > 0) sb.AppendLine($"  Properties: {string.Join(", ", properties)}");
                if (events.Count > 0) sb.AppendLine($"  Events: {string.Join(", ", events)}");
                if (fields.Count > 0) 
                {
                    var namesStr = fieldNames.Count > 5 
                        ? string.Join(", ", fieldNames.Take(5)) + ", ..."
                        : string.Join(", ", fieldNames);
                    sb.AppendLine($"  Fields: {fields.Count} ({namesStr})");
                }
                if (others.Count > 0) sb.AppendLine($"  Other: {string.Join(", ", others)}");
            }

            var outline = sb.ToString().TrimEnd();
            
            if (!ignoreLimits)
            {
                if (EstimateTokenCount(outline) > TokenCap)
                {
                    outline = TruncateOutline(outline, TokenCap);
                    isTruncatedByParser = true;
                }

                if (symbols.Count > MaxSymbols)
                {
                    var trimmed = symbols.Take(MaxSymbols).ToList();
                    symbols = trimmed;
                    outline += $"\n... ({symbols.Count - MaxSymbols} more symbols truncated)";
                    isTruncatedByParser = true;
                }
            }

            result = new SkeletonResult
            {
                Path = "",
                ContentHash = hash,
                Outline = outline,
                Symbols = symbols,
                StatusKind = isTruncatedByParser ? ContextStatus.SkeletonTruncated : ContextStatus.Skeleton,
                StatusDetail = isTruncatedByParser ? "SKELETON — TRUNCATED; remainder omitted" : "SKELETON — complete outline"
            };
        }
        catch (Exception)
        {
            result = ParseFallback(source, hash);
        }

        return result;
    }

    private static string BuildTypeHeader(SyntaxNode type, SyntaxTree tree)
    {
        var sb = new StringBuilder();
        var modifiers = type switch
        {
            ClassDeclarationSyntax c => string.Join(" ", c.Modifiers.Select(m => m.Text)),
            StructDeclarationSyntax s => string.Join(" ", s.Modifiers.Select(m => m.Text)),
            InterfaceDeclarationSyntax i => string.Join(" ", i.Modifiers.Select(m => m.Text)),
            EnumDeclarationSyntax e => string.Join(" ", e.Modifiers.Select(m => m.Text)),
            DelegateDeclarationSyntax d => string.Join(" ", d.Modifiers.Select(m => m.Text)),
            _ => ""
        };
        if (!string.IsNullOrEmpty(modifiers)) sb.Append(modifiers).Append(' ');

        sb.Append(type switch
        {
            ClassDeclarationSyntax => "class",
            StructDeclarationSyntax => "struct",
            InterfaceDeclarationSyntax => "interface",
            EnumDeclarationSyntax => "enum",
            DelegateDeclarationSyntax => "delegate",
            _ => "type"
        }).Append(' ');

        var name = type switch
        {
            ClassDeclarationSyntax c => c.Identifier.Text,
            StructDeclarationSyntax s => s.Identifier.Text,
            InterfaceDeclarationSyntax i => i.Identifier.Text,
            EnumDeclarationSyntax e => e.Identifier.Text,
            DelegateDeclarationSyntax d => d.Identifier.Text,
            _ => "?"
        };
        sb.Append(name);

        var typeParams = type switch
        {
            ClassDeclarationSyntax c => c.TypeParameterList?.ToString(),
            StructDeclarationSyntax s => s.TypeParameterList?.ToString(),
            InterfaceDeclarationSyntax i => i.TypeParameterList?.ToString(),
            DelegateDeclarationSyntax d => d.TypeParameterList?.ToString(),
            _ => null
        };
        if (!string.IsNullOrEmpty(typeParams)) sb.Append(typeParams);

        var baseType = type switch
        {
            ClassDeclarationSyntax c => c.BaseList?.ToString(),
            StructDeclarationSyntax s => s.BaseList?.ToString(),
            InterfaceDeclarationSyntax i => i.BaseList?.ToString(),
            EnumDeclarationSyntax e => e.BaseList?.ToString(),
            _ => null
        };
        if (!string.IsNullOrEmpty(baseType)) sb.Append(" : ").Append(baseType.Trim());

        var endLine = tree.GetLineSpan(type.Span).EndLinePosition.Line + 1;
        sb.Append($"  [L{tree.GetLineSpan(type.Span).StartLinePosition.Line + 1}-{endLine}]");
        return sb.ToString();
    }

    private static IEnumerable<SyntaxNode> GetTypeMembers(SyntaxNode type)
    {
        return type switch
        {
            ClassDeclarationSyntax c => c.Members,
            StructDeclarationSyntax s => s.Members,
            InterfaceDeclarationSyntax i => i.Members,
            EnumDeclarationSyntax e => e.Members,
            _ => Enumerable.Empty<SyntaxNode>()
        };
    }

    private static string GetMemberName(SyntaxNode member)
    {
        return member switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            PropertyDeclarationSyntax p => p.Identifier.Text,
            FieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "_",
            ConstructorDeclarationSyntax c => ".ctor",
            EventDeclarationSyntax e => e.Identifier.Text,
            ClassDeclarationSyntax c => c.Identifier.Text,
            StructDeclarationSyntax s => s.Identifier.Text,
            InterfaceDeclarationSyntax i => i.Identifier.Text,
            EnumDeclarationSyntax e => e.Identifier.Text,
            DelegateDeclarationSyntax d => d.Identifier.Text,
            IndexerDeclarationSyntax idx => "this[]",
            _ => "?"
        };
    }

    private static string GetMemberSignature(SyntaxNode member)
    {
        return member switch
        {
            MethodDeclarationSyntax m =>
                $"{string.Join(" ", m.Modifiers.Select(x => x.Text))} {m.ReturnType} {m.Identifier}{m.TypeParameterList}({FormatParams(m.ParameterList)})",
            PropertyDeclarationSyntax p =>
                $"{string.Join(" ", p.Modifiers.Select(x => x.Text))} {p.Type} {p.Identifier}",
            FieldDeclarationSyntax f =>
                $"{string.Join(" ", f.Modifiers.Select(x => x.Text))} {f.Declaration.Type} {f.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "_"}",
            ConstructorDeclarationSyntax c =>
                $"{string.Join(" ", c.Modifiers.Select(x => x.Text))} .ctor({FormatParams(c.ParameterList)})",
            EventDeclarationSyntax e =>
                $"{string.Join(" ", e.Modifiers.Select(x => x.Text))} {e.Type} {e.Identifier}",
            _ => ExtractGenericSignature(member)
        };
    }

    private static string ExtractGenericSignature(SyntaxNode member)
    {
        var str = member.ToString();
        var cutIndex = str.IndexOfAny(new[] { '{', '=', ';' });
        if (cutIndex >= 0)
        {
            str = str.Substring(0, cutIndex);
        }
        
        var lines = str.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", lines.Select(l => l.Trim())).Trim();
    }

    private static string FormatParams(ParameterListSyntax? paramList)
    {
        if (paramList == null) return "";
        var ps = paramList.Parameters;
        if (ps.Count == 0) return "";
        string joined;
        if (ps.Count <= 3) joined = string.Join(", ", ps.Select(p => $"{p.Type} {p.Identifier}"));
        else joined = string.Join(", ", ps.Take(3).Select(p => $"{p.Type} {p.Identifier}")) + ", ...";
        return joined.Trim();
    }

    private static int EstimateTokenCount(SyntaxNode node) =>
        EstimateTokenCount(node.ToFullString());

    private static int EstimateTokenCount(string text) =>
        Math.Max(1, text.Length / 4);

    private static string TruncateOutline(string outline, int cap)
    {
        var lines = outline.Split('\n');
        var sb = new StringBuilder();
        int tokens = 0;
        foreach (var line in lines)
        {
            tokens += Math.Max(1, line.Length / 4);
            if (tokens > cap) break;
            sb.AppendLine(line);
        }
        sb.AppendLine("... (truncated)");
        return sb.ToString().TrimEnd();
    }

    private static SkeletonResult ParseFallback(string source, string hash)
    {
        var lines = source.Split('\n');
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i += 20)
        {
            var window = lines.Skip(i).Take(20);
            sb.AppendLine($"# Lines {i + 1}-{Math.Min(i + 20, lines.Length)}");
            foreach (var line in window)
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed)) sb.AppendLine(trimmed);
            }
        }
        return new SkeletonResult
        {
            Path = "",
            ContentHash = hash,
            Outline = sb.ToString().TrimEnd(),
            Symbols = new List<SymbolNode>()
        };
    }
}
