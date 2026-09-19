using System;

namespace AiAssistant.Core.Models;

public record ApprovalRequest
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string ToolName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public object? Parameters { get; init; }
    public DateTime RequestedAt { get; init; } = DateTime.UtcNow;
    public bool IsApproved { get; init; } = false;
    public bool IsRejected { get; init; } = false;
}
