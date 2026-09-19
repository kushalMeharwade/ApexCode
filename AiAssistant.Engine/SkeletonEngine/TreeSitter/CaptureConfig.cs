using System;
using System.Collections.Generic;
using System.Linq;

namespace AiAssistant.Engine.SkeletonEngine.TreeSitter;

public sealed class CaptureRule
{
    public string NodeType { get; init; } = "";
    public SymbolKind Kind { get; init; }
    public bool CaptureBody { get; init; }
    public bool RecurseInto { get; init; }
}

public static class CaptureConfig
{
    private static readonly Dictionary<string, List<CaptureRule>> LanguageRules = new(StringComparer.OrdinalIgnoreCase)
    {
        [".py"] = new()
        {
            new() { NodeType = "import_declaration", Kind = SymbolKind.Using, CaptureBody = false, RecurseInto = false },
            new() { NodeType = "import_from_declaration", Kind = SymbolKind.Using, CaptureBody = false, RecurseInto = false },
            new() { NodeType = "class_definition", Kind = SymbolKind.Class, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "function_definition", Kind = SymbolKind.Method, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "decorated_definition", Kind = SymbolKind.Method, CaptureBody = false, RecurseInto = true },
        },
        [".js"] = new()
        {
            new() { NodeType = "import_statement", Kind = SymbolKind.Using, CaptureBody = false, RecurseInto = false },
            new() { NodeType = "class_declaration", Kind = SymbolKind.Class, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "function_declaration", Kind = SymbolKind.Method, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "method_definition", Kind = SymbolKind.Method, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "arrow_function", Kind = SymbolKind.Method, CaptureBody = false, RecurseInto = false },
            new() { NodeType = "export_statement", Kind = SymbolKind.Namespace, CaptureBody = false, RecurseInto = false },
            new() { NodeType = "public_field_definition", Kind = SymbolKind.Field, CaptureBody = false, RecurseInto = false },
        },
        [".ts"] = new()
        {
            new() { NodeType = "import_statement", Kind = SymbolKind.Using, CaptureBody = false, RecurseInto = false },
            new() { NodeType = "class_declaration", Kind = SymbolKind.Class, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "interface_declaration", Kind = SymbolKind.Interface, CaptureBody = false, RecurseInto = false },
            new() { NodeType = "type_alias_declaration", Kind = SymbolKind.Struct, CaptureBody = false, RecurseInto = false },
            new() { NodeType = "enum_declaration", Kind = SymbolKind.Enum, CaptureBody = false, RecurseInto = false },
            new() { NodeType = "function_declaration", Kind = SymbolKind.Method, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "method_definition", Kind = SymbolKind.Method, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "public_field_definition", Kind = SymbolKind.Field, CaptureBody = false, RecurseInto = false },
            new() { NodeType = "property_signature", Kind = SymbolKind.Field, CaptureBody = false, RecurseInto = false },
        },
        [".c"] = new()
        {
            new() { NodeType = "preproc_include", Kind = SymbolKind.Using, CaptureBody = false, RecurseInto = false },
            new() { NodeType = "struct_specifier", Kind = SymbolKind.Struct, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "function_definition", Kind = SymbolKind.Method, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "typedef_declaration", Kind = SymbolKind.Struct, CaptureBody = false, RecurseInto = false },
            new() { NodeType = "enum_specifier", Kind = SymbolKind.Enum, CaptureBody = false, RecurseInto = false },
            new() { NodeType = "field_declaration", Kind = SymbolKind.Field, CaptureBody = false, RecurseInto = false },
        },
        [".cpp"] = new()
        {
            new() { NodeType = "preproc_include", Kind = SymbolKind.Using, CaptureBody = false, RecurseInto = false },
            new() { NodeType = "class_specifier", Kind = SymbolKind.Class, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "struct_specifier", Kind = SymbolKind.Struct, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "function_definition", Kind = SymbolKind.Method, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "namespace_definition", Kind = SymbolKind.Namespace, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "enum_specifier", Kind = SymbolKind.Enum, CaptureBody = false, RecurseInto = false },
            new() { NodeType = "field_declaration", Kind = SymbolKind.Field, CaptureBody = false, RecurseInto = false },
        },
        [".go"] = new()
        {
            new() { NodeType = "import_declaration", Kind = SymbolKind.Using, CaptureBody = false, RecurseInto = false },
            new() { NodeType = "type_declaration", Kind = SymbolKind.Struct, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "function_declaration", Kind = SymbolKind.Method, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "method_declaration", Kind = SymbolKind.Method, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "field_declaration", Kind = SymbolKind.Field, CaptureBody = false, RecurseInto = false },
        },
        [".rs"] = new()
        {
            new() { NodeType = "use_declaration", Kind = SymbolKind.Using, CaptureBody = false, RecurseInto = false },
            new() { NodeType = "mod_item", Kind = SymbolKind.Namespace, CaptureBody = false, RecurseInto = false },
            new() { NodeType = "struct_item", Kind = SymbolKind.Struct, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "enum_item", Kind = SymbolKind.Enum, CaptureBody = false, RecurseInto = false },
            new() { NodeType = "trait_item", Kind = SymbolKind.Interface, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "impl_item", Kind = SymbolKind.Class, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "function_item", Kind = SymbolKind.Method, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "field_declaration", Kind = SymbolKind.Field, CaptureBody = false, RecurseInto = false },
        },
        [".java"] = new()
        {
            new() { NodeType = "import_declaration", Kind = SymbolKind.Using, CaptureBody = false, RecurseInto = false },
            new() { NodeType = "package_declaration", Kind = SymbolKind.Namespace, CaptureBody = false, RecurseInto = false },
            new() { NodeType = "class_declaration", Kind = SymbolKind.Class, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "interface_declaration", Kind = SymbolKind.Interface, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "enum_declaration", Kind = SymbolKind.Enum, CaptureBody = false, RecurseInto = false },
            new() { NodeType = "method_declaration", Kind = SymbolKind.Method, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "constructor_declaration", Kind = SymbolKind.Constructor, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "field_declaration", Kind = SymbolKind.Field, CaptureBody = false, RecurseInto = false },
        },
        [".php"] = new()
        {
            new() { NodeType = "class_declaration", Kind = SymbolKind.Class, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "method_declaration", Kind = SymbolKind.Method, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "function_declaration", Kind = SymbolKind.Method, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "property_declaration", Kind = SymbolKind.Field, CaptureBody = false, RecurseInto = false },
        },
        [".rb"] = new()
        {
            new() { NodeType = "class", Kind = SymbolKind.Class, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "module", Kind = SymbolKind.Namespace, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "method", Kind = SymbolKind.Method, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "singleton_method", Kind = SymbolKind.Method, CaptureBody = false, RecurseInto = true },
        },
        [".sh"] = new()
        {
            new() { NodeType = "function_definition", Kind = SymbolKind.Method, CaptureBody = false, RecurseInto = true },
        },
        [".css"] = new()
        {
            new() { NodeType = "rule_set", Kind = SymbolKind.Class, CaptureBody = false, RecurseInto = false },
        },
        [".json"] = new()
        {
            new() { NodeType = "pair", Kind = SymbolKind.Field, CaptureBody = false, RecurseInto = true },
        },
        [".toml"] = new()
        {
            new() { NodeType = "table", Kind = SymbolKind.Class, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "pair", Kind = SymbolKind.Field, CaptureBody = false, RecurseInto = false },
        },
        [".yaml"] = new()
        {
            new() { NodeType = "block_mapping_pair", Kind = SymbolKind.Field, CaptureBody = false, RecurseInto = true },
        },
        [".md"] = new()
        {
            new() { NodeType = "atx_heading", Kind = SymbolKind.Method, CaptureBody = false, RecurseInto = false },
        },
        [".html"] = new()
        {
            new() { NodeType = "element", Kind = SymbolKind.Class, CaptureBody = false, RecurseInto = true },
            new() { NodeType = "script_element", Kind = SymbolKind.Method, CaptureBody = false, RecurseInto = false },
            new() { NodeType = "style_element", Kind = SymbolKind.Method, CaptureBody = false, RecurseInto = false },
        },
    };

    private static readonly HashSet<string> NestingKinds = new()
    {
        "class_definition", "class_declaration", "class_specifier", "class",
        "struct_specifier", "struct_item", "type_declaration", "struct_type",
        "interface_declaration", "interface_item",
        "enum_declaration", "enum_specifier", "enum_item",
        "namespace_definition", "mod_item", "module",
        "impl_item", "trait_item",
        "function_definition", "function_declaration", "function_item",
        "method_definition", "method_declaration", "method", "singleton_method",
        "decorated_definition",
        "arrow_function",
        "rule_set", "table", "pair", "block_mapping_pair",
        "element"
    };

    public static IReadOnlyList<CaptureRule> GetRules(string extension)
    {
        return LanguageRules.TryGetValue(extension, out var rules)
            ? rules
            : Array.Empty<CaptureRule>();
    }

    public static bool IsNestingType(string nodeType)
    {
        return NestingKinds.Contains(nodeType);
    }

    public static string[] GetAllSupportedExtensions()
    {
        return LanguageRules.Keys.ToArray();
    }
}
