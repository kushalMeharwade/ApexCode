using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AiAssistant.Engine.SkeletonEngine;

public sealed class HiddenContextStore
{
    private readonly Dictionary<string, SkeletonResult> _store = new(StringComparer.OrdinalIgnoreCase);

    public void Attach(string path, SkeletonResult skeleton)
    {
        _store[path] = skeleton;
    }

    public IReadOnlyDictionary<string, SkeletonResult> GetAttached() => _store;

    public void Clear()
    {
        _store.Clear();
    }

    public void Remove(string path)
    {
        _store.Remove(path);
    }

    public string SerializeAll()
    {
        if (_store.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("--- hidden context: file skeletons ---");
        foreach (var kvp in _store)
        {
            var path = kvp.Key;
            var skeleton = kvp.Value;
            sb.AppendLine($"--- skeleton: {path} ---");
            sb.AppendLine(skeleton.Outline);
            sb.AppendLine();
        }
        sb.AppendLine("--- end hidden context ---");
        return sb.ToString();
    }
}
