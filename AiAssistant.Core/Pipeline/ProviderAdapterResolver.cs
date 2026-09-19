namespace AiAssistant.Core.Pipeline;

public sealed class ProviderAdapterResolver(IEnumerable<IProviderAdapter> adapters)
{
    private readonly IReadOnlyDictionary<string, IProviderAdapter> _adapters = adapters
        .ToDictionary(adapter => adapter.ProviderName, StringComparer.OrdinalIgnoreCase);

    public IProviderAdapter Resolve(string providerType)
    {
        var normalized = providerType?.Trim() ?? string.Empty;
        if (_adapters.TryGetValue(normalized, out var adapter))
            return adapter;

        if (normalized is "azureopenai" or "openrouter" or "ollama")
            return _adapters["openai"];

        throw new NotSupportedException($"No request adapter is registered for provider '{providerType}'.");
    }
}
