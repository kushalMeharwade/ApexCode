using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using AiAssistant.Core.Services;

namespace AiAssistant.Core.Models;

/// <summary>
/// Lifecycle of a proposed plan from the reviewer's point of view.
/// </summary>
public enum PlanReviewStatus
{
    /// <summary>No plan has been proposed for the session.</summary>
    None = 0,

    /// <summary>A plan is on the table and the user has not decided yet.</summary>
    AwaitingReview = 1,

    /// <summary>The user sent feedback and a revised plan is being generated.</summary>
    RevisionRequested = 2,

    /// <summary>The user approved the plan; execution has been handed to Act mode.</summary>
    Approved = 3,

    /// <summary>
    /// The plan was recovered from conversation history where the reviewer decision was not
    /// recorded. Kept for reference, but no longer actionable.
    /// </summary>
    Archived = 4,
}

/// <summary>
/// One proposal round: the steps the model produced plus whatever the reviewer did with them.
/// </summary>
public sealed class PlanRevisionRecord
{
    /// <summary>1-based proposal round. Incremented every time the model re-proposes.</summary>
    public int Revision { get; set; } = 1;

    public List<PlanItem> Items { get; set; } = new();

    public PlanReviewStatus Status { get; set; } = PlanReviewStatus.AwaitingReview;

    /// <summary>Free-form reviewer instructions that are not tied to a single step.</summary>
    public string? GeneralComment { get; set; }

    public DateTime ProposedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>When the reviewer approved this revision or asked for changes.</summary>
    public DateTime? DecidedAtUtc { get; set; }
}

/// <summary>
/// Durable plan-review state for a single chat session. Persisted by
/// <see cref="Services.IPlanStateStore"/> so an approved plan stays viewable after the
/// approval overlay closes, across session switches and across IDE restarts.
/// </summary>
public sealed class PlanSessionState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string SessionId { get; set; } = string.Empty;

    /// <summary>Last agent mode the user selected for this session ("Plan" or "Act").</summary>
    public string ActiveMode { get; set; } = AgentModes.Plan;

    /// <summary>All proposal rounds in chronological order.</summary>
    public List<PlanRevisionRecord> Revisions { get; set; } = new();

    /// <summary>The most recent proposal round, or <c>null</c> when none exists.</summary>
    [JsonIgnore]
    public PlanRevisionRecord? Latest => Revisions.Count == 0 ? null : Revisions[Revisions.Count - 1];

    /// <summary>
    /// Inserts <paramref name="record"/>, replacing an existing round with the same revision
    /// number so repeated saves during a single review do not grow the history.
    /// </summary>
    public void UpsertRevision(PlanRevisionRecord record)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));

        for (int i = 0; i < Revisions.Count; i++)
        {
            if (Revisions[i].Revision == record.Revision)
            {
                Revisions[i] = record;
                return;
            }
        }

        Revisions.Add(record);
        Revisions.Sort((left, right) => left.Revision.CompareTo(right.Revision));
    }
}
