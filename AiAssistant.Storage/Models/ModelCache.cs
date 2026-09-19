using System;

namespace AiAssistant.Storage.Models;

public record ModelCache
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string ProviderId { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public int ContextWindowTokens { get; init; } = 0;
    public DateTime CachedAt { get; init; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; init; } = DateTime.UtcNow.AddDays(7);
}
