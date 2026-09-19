namespace AiAssistant.Core.Pipeline;

/// <summary>
/// A small keyed LRU cache for built system prompts.
/// Capacity is intentionally tiny (4 entries) because the stable prefix changes only when
/// the persona, workspace, or tool registration changes — not on every tab switch or turn.
/// Two alternating contexts (Plan ↔ Act) previously thrashed the old single-slot cache,
/// causing a full prompt rebuild (and cache miss at the provider) on every request.
/// </summary>
public sealed class SystemPromptCache(SystemPromptBuilder builder)
{
    private const int Capacity = 4;
    private readonly object _gate = new();

    // Ordered list of (hash, prompt) pairs, newest at end
    private readonly List<(string Hash, string Prompt)> _entries = new(Capacity);

    public string GetOrBuild(ContextState context)
    {
        if (context is null)
            throw new ArgumentNullException(nameof(context));

        var hash = context.StableHash;

        lock (_gate)
        {
            // Look for existing entry
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_entries[i].Hash, hash, StringComparison.Ordinal))
                {
                    // Move to end (most recently used)
                    var hit = _entries[i];
                    _entries.RemoveAt(i);
                    _entries.Add(hit);
                    return hit.Prompt;
                }
            }

            // Cache miss — build and store
            var prompt = builder.Build(context);
            if (_entries.Count >= Capacity)
                _entries.RemoveAt(0); // evict LRU (oldest = front)
            _entries.Add((hash, prompt));
            return prompt;
        }
    }

    public void Invalidate()
    {
        lock (_gate) { _entries.Clear(); }
    }
}
