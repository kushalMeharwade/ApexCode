using System;

namespace AiAssistant.Storage.Models;

public record ProviderProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Name { get; init; } = string.Empty;
    public string ProviderType { get; init; } = string.Empty;
    public string? ApiEndpoint { get; init; }
    public string? ModelFetchEndpoint { get; init; }
    public string? ApiKey { get; init; }
    public string? DefaultModel { get; init; }
    public int ContextWindowTokens { get; init; } = 1000000;
    public bool IsEnabled { get; init; } = true;
    public bool IsBuiltIn { get; init; } = false;
    public string? LogoResourceKey { get; init; }
    public string? BuiltInId { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
