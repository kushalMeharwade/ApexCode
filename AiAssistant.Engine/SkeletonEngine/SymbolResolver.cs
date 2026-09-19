using System;
using System.Collections.Generic;
using System.Linq;

namespace AiAssistant.Engine.SkeletonEngine;

public static class SymbolResolver
{
    public static SymbolNode? Find(IReadOnlyList<SymbolNode> symbols, string name)
    {
        foreach (var symbol in symbols)
        {
            if (string.Equals(symbol.Name, name, StringComparison.OrdinalIgnoreCase))
                return symbol;
            var found = Find(symbol.Children, name);
            if (found != null) return found;
        }
        return null;
    }

    public static string ReadSymbolBody(string source, SymbolNode node)
    {
        var lines = source.Split('\n');
        if (node.StartLine < 1 || node.EndLine > lines.Length) return "";
        var start = Math.Max(0, node.StartLine - 1);
        var count = Math.Min(lines.Length - start, node.EndLine - node.StartLine + 1);
        return string.Join("\n", lines.Skip(start).Take(count));
    }
}
