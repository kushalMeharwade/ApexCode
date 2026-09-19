using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace AiAssistant.Engine.SkeletonEngine.TreeSitter;

[StructLayout(LayoutKind.Sequential)]
public struct TSNode
{
    public uint context0;
    public uint context1;
    public uint context2;
    public uint context3;
    public IntPtr id;
    public IntPtr tree;
}

[StructLayout(LayoutKind.Sequential)]
public struct TSPoint
{
    public uint row;
    public uint column;
}

public static class TreeSitterApi
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr TsParserNew();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void TsParserDelete(IntPtr parser);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate bool TsParserSetLanguage(IntPtr parser, IntPtr language);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr TsParserParseString(IntPtr parser, IntPtr oldTree, IntPtr bytes, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate TSNode TsTreeRootNode(IntPtr tree);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void TsTreeDelete(IntPtr tree);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr TsLanguageSymbolCount(IntPtr language);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr TsLanguageSymbolName(IntPtr language, ushort id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate ushort TsLanguageSymbolForName(IntPtr language, IntPtr name, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr TsNodeString(TSNode node);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate uint TsNodeStartByte(TSNode node);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate uint TsNodeEndByte(TSNode node);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate TSPoint TsNodeStartPoint(TSNode node);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate TSPoint TsNodeEndPoint(TSNode node);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr TsNodeSymbol(TSNode node);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate uint TsNodeChildCount(TSNode node);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate TSNode TsNodeChild(TSNode node, uint index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate TSNode TsNodeParent(TSNode node);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate TSNode TsNodeNextSibling(TSNode node);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate TSNode TsNodePrevSibling(TSNode node);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr TsNodeTypeName(TSNode node);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate bool TsNodeIsNamed(TSNode node);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate bool TsNodeHasError(TSNode node);

    public static TsParserNew? ParserNew;
    public static TsParserDelete? ParserDelete;
    public static TsParserSetLanguage? ParserSetLanguage;
    public static TsParserParseString? ParserParseString;
    public static TsTreeRootNode? TreeRootNode;
    public static TsTreeDelete? TreeDelete;
    public static TsLanguageSymbolCount? LanguageSymbolCount;
    public static TsLanguageSymbolName? LanguageSymbolName;
    public static TsLanguageSymbolForName? LanguageSymbolForName;
    public static TsNodeString? NodeString;
    public static TsNodeStartByte? NodeStartByte;
    public static TsNodeEndByte? NodeEndByte;
    public static TsNodeStartPoint? NodeStartPoint;
    public static TsNodeEndPoint? NodeEndPoint;
    public static TsNodeSymbol? NodeSymbol;
    public static TsNodeChildCount? NodeChildCount;
    public static TsNodeChild? NodeChild;
    public static TsNodeParent? NodeParent;
    public static TsNodeNextSibling? NodeNextSibling;
    public static TsNodePrevSibling? NodePrevSibling;
    public static TsNodeTypeName? NodeTypeName;
    public static TsNodeIsNamed? NodeIsNamed;
    public static TsNodeHasError? NodeHasError;

    public static void Load(IntPtr module)
    {
        ParserNew = NativeLoader.GetFunction<TsParserNew>(module, "ts_parser_new");
        ParserDelete = NativeLoader.GetFunction<TsParserDelete>(module, "ts_parser_delete");
        ParserSetLanguage = NativeLoader.GetFunction<TsParserSetLanguage>(module, "ts_parser_set_language");
        ParserParseString = NativeLoader.GetFunction<TsParserParseString>(module, "ts_parser_parse_string");
        TreeRootNode = NativeLoader.GetFunction<TsTreeRootNode>(module, "ts_tree_root_node");
        TreeDelete = NativeLoader.GetFunction<TsTreeDelete>(module, "ts_tree_delete");
        LanguageSymbolCount = NativeLoader.GetFunction<TsLanguageSymbolCount>(module, "ts_language_symbol_count");
        LanguageSymbolName = NativeLoader.GetFunction<TsLanguageSymbolName>(module, "ts_language_symbol_name");
        LanguageSymbolForName = NativeLoader.GetFunction<TsLanguageSymbolForName>(module, "ts_language_symbol_for_name");
        NodeString = NativeLoader.GetFunction<TsNodeString>(module, "ts_node_string");
        NodeStartByte = NativeLoader.GetFunction<TsNodeStartByte>(module, "ts_node_start_byte");
        NodeEndByte = NativeLoader.GetFunction<TsNodeEndByte>(module, "ts_node_end_byte");
        NodeStartPoint = NativeLoader.GetFunction<TsNodeStartPoint>(module, "ts_node_start_point");
        NodeEndPoint = NativeLoader.GetFunction<TsNodeEndPoint>(module, "ts_node_end_point");
        NodeSymbol = NativeLoader.GetFunction<TsNodeSymbol>(module, "ts_node_symbol");
        NodeChildCount = NativeLoader.GetFunction<TsNodeChildCount>(module, "ts_node_child_count");
        NodeChild = NativeLoader.GetFunction<TsNodeChild>(module, "ts_node_child");
        NodeParent = NativeLoader.GetFunction<TsNodeParent>(module, "ts_node_parent");
        NodeNextSibling = NativeLoader.GetFunction<TsNodeNextSibling>(module, "ts_node_next_sibling");
        NodePrevSibling = NativeLoader.GetFunction<TsNodePrevSibling>(module, "ts_node_prev_sibling");
        NodeTypeName = NativeLoader.GetFunction<TsNodeTypeName>(module, "ts_node_type");
        NodeIsNamed = NativeLoader.GetFunction<TsNodeIsNamed>(module, "ts_node_is_named");
        NodeHasError = NativeLoader.GetFunction<TsNodeHasError>(module, "ts_node_has_error");
    }
}
