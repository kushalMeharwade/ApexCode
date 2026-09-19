using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using AiAssistant.Engine.SkeletonEngine.TreeSitter;

namespace AiAssistant.Engine.SkeletonEngine;

public sealed class TreeSitterParser : ISkeletonParser
{
    private readonly string _extension;

    public TreeSitterParser(string extension)
    {
        _extension = extension;
    }

    public bool Supports(string extension) =>
        TreeSitterLanguages.IsSupported(extension);

    public SkeletonResult Parse(string source, string extension, bool ignoreLimits = false)
    {
        var hash = ContentHash.Compute(source);
        try
        {
            TreeSitterLanguages.Initialize();
            var language = TreeSitterLanguages.GetLanguage(extension);
            if (language == IntPtr.Zero)
            {
                return ParseFallback(source, hash, $"No tree-sitter grammar for {extension}");
            }

            var parser = TreeSitterApi.ParserNew!();
            if (parser == IntPtr.Zero)
            {
                return ParseFallback(source, hash, "Failed to create tree-sitter parser");
            }

            try
            {
                if (!TreeSitterApi.ParserSetLanguage!(parser, language))
                {
                    return ParseFallback(source, hash, "Failed to set tree-sitter language");
                }

                var bytes = Encoding.UTF8.GetBytes(source);
                var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                IntPtr tree;
                try
                {
                    tree = TreeSitterApi.ParserParseString!(parser, IntPtr.Zero, handle.AddrOfPinnedObject(), (uint)bytes.Length);
                }
                finally
                {
                    handle.Free();
                }

                if (tree == IntPtr.Zero)
                {
                    return ParseFallback(source, hash, "Tree-sitter parse returned null");
                }

                try
                {
                    var rootNode = TreeSitterApi.TreeRootNode!(tree);
                    if (rootNode.id == IntPtr.Zero)
                    {
                        return ParseFallback(source, hash, "Tree-sitter root node is null");
                    }

                    var rules = CaptureConfig.GetRules(extension);
                    var symbols = new List<SymbolNode>();
                    WalkTree(rootNode, rules, symbols, null, 0, 3, source);
                    var sb = new StringBuilder();
                    bool isTruncatedByParser = false;
                    FormatSymbolTree(symbols, sb, ref isTruncatedByParser, ignoreLimits);

                    var outline = sb.ToString().TrimEnd();
                    return new SkeletonResult
                    {
                        Path = "",
                        ContentHash = hash,
                        Outline = outline,
                        Symbols = symbols,
                        StatusKind = isTruncatedByParser ? ContextStatus.SkeletonTruncated : ContextStatus.Skeleton,
                        StatusDetail = isTruncatedByParser ? "SKELETON — TRUNCATED; remainder omitted" : "SKELETON — complete outline"
                    };
                }
                finally
                {
                    TreeSitterApi.TreeDelete!(tree);
                }
            }
            finally
            {
                TreeSitterApi.ParserDelete!(parser);
            }
        }
        catch (Exception ex)
        {
            return ParseFallback(source, hash, $"Tree-sitter error: {ex.Message}");
        }
    }

    private static void WalkTree(TSNode node, IReadOnlyList<CaptureRule> rules, List<SymbolNode> topLevelSymbols, SymbolNode? parent, int depth, int maxDepth, string source)
    {
        if (node.id == IntPtr.Zero || depth > maxDepth) return;

        var childCount = (int)TreeSitterApi.NodeChildCount!(node);
        for (var i = 0; i < childCount; i++)
        {
            var child = TreeSitterApi.NodeChild!(node, (uint)i);
            if (child.id == IntPtr.Zero) continue;

            var typePtr = TreeSitterApi.NodeTypeName!(child);
            var nodeType = PtrToString(typePtr) ?? "";

            if (TryCapture(child, nodeType, rules, out var symbol, source))
            {
                bool isContainer = symbol!.Kind == SymbolKind.Namespace || 
                                   symbol.Kind == SymbolKind.Class || 
                                   symbol.Kind == SymbolKind.Struct || 
                                   symbol.Kind == SymbolKind.Interface || 
                                   symbol.Kind == SymbolKind.Enum;

                if (isContainer || parent == null)
                {
                    topLevelSymbols.Add(symbol);
                }
                else
                {
                    parent.Children.Add(symbol);
                }

                if (depth < maxDepth && CaptureConfig.IsNestingType(nodeType))
                {
                    WalkTree(child, rules, topLevelSymbols, isContainer ? symbol : parent, depth + 1, maxDepth, source);
                }

                if (parent != null && (parent.Kind == SymbolKind.Class || parent.Kind == SymbolKind.Struct) && 
                    (symbol.Name == "__init__" || symbol.Name == "constructor" || symbol.Name == "initialize"))
                {
                    var start = (int)TreeSitterApi.NodeStartByte!(child);
                    var end = (int)TreeSitterApi.NodeEndByte!(child);
                    if (start >= 0 && end > start && end <= source.Length)
                    {
                        var body = source.Substring(start, end - start);
                        var matches = System.Text.RegularExpressions.Regex.Matches(body, @"(?:(?:\b(?:self|this)\.)|@)([a-zA-Z_][a-zA-Z0-9_]*)\s*=");
                        var uniqueFields = new HashSet<string>();
                        foreach (System.Text.RegularExpressions.Match match in matches)
                        {
                            if (match.Groups.Count > 1) uniqueFields.Add(match.Groups[1].Value);
                        }
                        foreach (var fieldName in uniqueFields)
                        {
                            // Avoid duplicating fields if already captured
                            if (!parent.Children.Any(c => c.Kind == SymbolKind.Field && c.Name == fieldName))
                            {
                                parent.Children.Add(new SymbolNode(SymbolKind.Field, fieldName, fieldName, symbol.StartLine, symbol.EndLine));
                            }
                        }
                    }
                }
            }
            else
            {
                WalkTree(child, rules, topLevelSymbols, parent, depth, maxDepth, source);
            }
        }
    }

    private static bool TryCapture(TSNode node, string nodeType, IReadOnlyList<CaptureRule> rules, out SymbolNode? symbol, string source)
    {
        symbol = null;
        if (TreeSitterApi.NodeChildCount!(node) == 0) return false;

        foreach (var rule in rules)
        {
            if (string.Equals(rule.NodeType, nodeType, StringComparison.OrdinalIgnoreCase))
            {
                if (nodeType.EndsWith("_specifier"))
                {
                    bool hasBody = false;
                    var childCount = (int)TreeSitterApi.NodeChildCount!(node);
                    for (var i = 0; i < childCount; i++)
                    {
                        var child = TreeSitterApi.NodeChild!(node, (uint)i);
                        if (child.id == IntPtr.Zero) continue;
                        var typePtr = TreeSitterApi.NodeTypeName!(child);
                        var childType = PtrToString(typePtr) ?? "";
                        if (childType.EndsWith("_list") || childType == "declaration_list")
                        {
                            hasBody = true;
                            break;
                        }
                    }
                    if (!hasBody) return false;
                }

                var name = ExtractNodeName(node, source);
                
                var inheritance = "";
                if (rule.Kind == SymbolKind.Class || rule.Kind == SymbolKind.Struct || rule.Kind == SymbolKind.Interface)
                {
                    inheritance = ExtractInheritance(node, source);
                }

                var startLine = (int)TreeSitterApi.NodeStartPoint!(node).row + 1;
                var endLine = (int)TreeSitterApi.NodeEndPoint!(node).row + 1;
                var signature = BuildSignature(node, nodeType, name, inheritance);
                symbol = new SymbolNode(rule.Kind, name, signature, startLine, endLine);
                return true;
            }
        }
        return false;
    }

    private static void FormatSymbolTree(List<SymbolNode> symbols, StringBuilder sb, ref bool isTruncatedByParser, bool ignoreLimits)
    {
        foreach (var symbol in symbols)
        {
            if (symbol.Kind == SymbolKind.Using)
                continue; // Omit usings from outline

            var fields = new List<string>();
            var fieldNames = new List<string>();
            var properties = new List<string>();
            var constructors = new List<string>();
            var methods = new List<string>();
            var events = new List<string>();
            var others = new List<string>();

            foreach (var child in symbol.Children)
            {
                var sig = child.Signature.Trim();
                sig = child.StartLine == child.EndLine 
                    ? $"{sig} [L{child.StartLine}]" 
                    : $"{sig} [L{child.StartLine}-L{child.EndLine}]";
                    
                switch (child.Kind)
                {
                    case SymbolKind.Field: 
                        fields.Add(sig); 
                        fieldNames.Add(child.Name);
                        break;
                    case SymbolKind.Property: properties.Add(sig); break;
                    case SymbolKind.Constructor: constructors.Add(sig); break;
                    case SymbolKind.Method: methods.Add(sig); break;
                    case SymbolKind.Event: events.Add(sig); break;
                    default: others.Add(sig); break;
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

            var kindName = symbol.Kind switch {
                SymbolKind.Class => "Class",
                SymbolKind.Struct => "Struct",
                SymbolKind.Interface => "Interface",
                SymbolKind.Enum => "Enum",
                SymbolKind.Delegate => "Delegate",
                _ => "Type"
            };

            var summary = $"{kindName} {symbol.Name} — {fields.Count} fields, {properties.Count} properties, {constructors.Count} constructors, {totalMethods} methods";
            if (methodTruncated)
            {
                summary += " (outline truncated; showing first 50 methods).";
            }
            else
            {
                summary += ".";
            }

            sb.AppendLine(symbol.Signature);
            if (symbol.Kind != SymbolKind.Method && symbol.Kind != SymbolKind.Field && symbol.Kind != SymbolKind.Property)
            {
                sb.AppendLine($"  {summary}");
            }

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
            
            if (symbol.Children.Count > 0) sb.AppendLine();
        }
    }

    private static string? PtrToString(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return null;
        var bytes = new List<byte>();
        var p = ptr;
        while (true)
        {
            var b = Marshal.ReadByte(p);
            if (b == 0) break;
            bytes.Add(b);
            p = IntPtr.Add(p, 1);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static string ExtractNodeName(TSNode node, string source, int depth = 0)
    {
        if (depth > 5) return "?";
        var childCount = (int)TreeSitterApi.NodeChildCount!(node);
        for (var i = 0; i < childCount; i++)
        {
            var child = TreeSitterApi.NodeChild!(node, (uint)i);
            if (child.id == IntPtr.Zero) continue;
            var typePtr = TreeSitterApi.NodeTypeName!(child);
            var childType = PtrToString(typePtr) ?? "";
            if (string.Equals(childType, "identifier", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(childType, "field_identifier", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(childType, "property_identifier", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(childType, "type_identifier", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(childType, "variable_name", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(childType, "constant", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(childType, "word", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(childType, "key", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(childType, "bare_key", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(childType, "string_scalar", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(childType, "inline", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(childType, "table_identifier", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(childType, "name", StringComparison.OrdinalIgnoreCase))
            {
                var start = (int)TreeSitterApi.NodeStartByte!(child);
                var end = (int)TreeSitterApi.NodeEndByte!(child);
                if (start >= 0 && end > start && end <= source.Length)
                {
                    return source.Substring(start, end - start).Split('\n')[0].Trim();
                }
            }
            
            if (childType.Contains("declarator") || childType.Contains("spec") || 
                childType.Contains("element") || childType.Contains("node") || childType.Contains("scalar"))
            {
                var nestedName = ExtractNodeName(child, source, depth + 1);
                if (nestedName != "?") return nestedName;
            }
        }
        return "?";
    }

    private static string ExtractInheritance(TSNode node, string source)
    {
        var childCount = (int)TreeSitterApi.NodeChildCount!(node);
        for (var i = 0; i < childCount; i++)
        {
            var child = TreeSitterApi.NodeChild!(node, (uint)i);
            if (child.id == IntPtr.Zero) continue;
            var typePtr = TreeSitterApi.NodeTypeName!(child);
            var childType = PtrToString(typePtr) ?? "";
            
            if (childType == "base_clause" || childType == "base_list" || childType == "class_heritage" ||
                childType == "superclass" || childType == "interfaces" || childType == "argument_list")
            {
                var start = (int)TreeSitterApi.NodeStartByte!(child);
                var end = (int)TreeSitterApi.NodeEndByte!(child);
                if (start >= 0 && end > start && end <= source.Length)
                {
                    var rawText = source.Substring(start, end - start).Replace("\n", " ").Replace("\r", " ").Trim();
                    if (childType == "argument_list" && rawText.StartsWith("(") && rawText.EndsWith(")"))
                    {
                        rawText = rawText.Substring(1, rawText.Length - 2);
                        return $" extends {rawText}";
                    }
                    if (childType == "superclass")
                    {
                        if (rawText.StartsWith("<")) return $" extends {rawText.Substring(1).Trim()}";
                        if (rawText.StartsWith("extends")) return $" {rawText}";
                        return $" extends {rawText}";
                    }
                    if (childType == "interfaces")
                    {
                        if (rawText.StartsWith("implements")) return $" {rawText}";
                        return $" implements {rawText}";
                    }
                    if (childType == "base_clause" || childType == "base_list")
                    {
                        if (rawText.StartsWith("extends")) return $" {rawText}";
                        if (rawText.StartsWith(":")) rawText = rawText.Substring(1).Trim();
                        return $" : {rawText}";
                    }
                    if (childType == "class_heritage")
                    {
                        return $" {rawText}";
                    }
                }
            }
        }
        return "";
    }

    private static string BuildSignature(TSNode node, string nodeType, string name, string inheritance = "")
    {
        var sb = new StringBuilder();
        var prefix = nodeType switch
        {
            "class_definition" or "class_declaration" or "class_specifier" => "class ",
            "struct_specifier" or "struct_item" => "struct ",
            "interface_declaration" or "interface_item" or "trait_item" => "interface ",
            "enum_declaration" or "enum_specifier" or "enum_item" => "enum ",
            "impl_item" => "impl ",
            "type_alias_declaration" or "type_declaration" => "type ",
            "function_definition" or "function_declaration" or "function_item" or "method_definition" or "method_declaration" => "def ",
            "import_declaration" or "import_statement" or "import_from_declaration" or "preproc_include" or "use_declaration" => "using ",
            "decorated_definition" => "def ",
            _ => ""
        };
        sb.Append(prefix).Append(name).Append(inheritance);

        var startLine = (int)TreeSitterApi.NodeStartPoint!(node).row + 1;
        var endLine = (int)TreeSitterApi.NodeEndPoint!(node).row + 1;
        sb.Append($"  [L{startLine}-{endLine}]");
        return sb.ToString();
    }

    private static SkeletonResult ParseFallback(string source, string hash, string reason)
    {
        var lines = source.Split('\n');
        var sb = new StringBuilder();
        sb.AppendLine($"# Fallback outline ({reason})");
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
