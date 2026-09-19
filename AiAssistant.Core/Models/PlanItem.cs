using System.Text.Json.Serialization;

namespace AiAssistant.Core.Models;

/// <summary>
/// A single step of an agent-proposed implementation plan.
/// </summary>
/// <remarks>
/// <see cref="Title"/> and <see cref="Description"/> are authored by the model through the
/// <c>plan_mode_respond</c> tool. <see cref="Comment"/> is authored by the <em>user</em> during
/// plan review and is never produced by the model — it is the mechanism that carries reviewer
/// feedback into the next plan revision, or into the Act-mode execution message.
/// </remarks>
public record PlanItem
{
    public string Title { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Reviewer feedback attached to this step, or <c>null</c> when the user did not comment.
    /// </summary>
    public string? Comment { get; init; }

    public string[]? ToolsRequired { get; init; }

    public bool RequiresConfirmation { get; init; }

    /// <summary>True when the user attached non-whitespace feedback to this step.</summary>
    [JsonIgnore]
    public bool HasComment => !string.IsNullOrWhiteSpace(Comment);
}
