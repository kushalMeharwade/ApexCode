using System;

namespace AiAssistant.Storage.Models;

public record Session
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string ProviderId { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string SystemPromptId { get; init; } = string.Empty;
    public bool IsActive { get; init; } = false;
    public string ActiveMode { get; set; } = "Plan";
}
