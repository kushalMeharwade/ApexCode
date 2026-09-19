using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace AiAssistant.Engine.SkeletonEngine;

public sealed class SkeletonCache
{
    private readonly ConcurrentDictionary<string, SkeletonResult> _cache = new();

    public SkeletonResult GetOrBuild(string path, string source)
    {
        var hash = ContentHash.Compute(source);
        var key = $"{path}::{hash}";
        return _cache.GetOrAdd(key, _ => ParserRouter.Parse(path, source).CloneWith(path));
    }

    public void Invalidate(string path)
    {
        var toRemove = _cache.Keys.Where(k => k.StartsWith(path + "::", StringComparison.Ordinal)).ToList();
        foreach (var key in toRemove)
        {
            _cache.TryRemove(key, out _);
        }
    }

    public void Clear()
    {
        _cache.Clear();
    }
}
