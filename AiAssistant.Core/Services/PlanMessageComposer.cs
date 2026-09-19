using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AiAssistant.Core.Models;

namespace AiAssistant.Core.Services;

/// <summary>
/// Builds the conversation turn that carries a plan-review decision back to the model.
/// </summary>
/// <remarks>
/// The reviewer's comments used to be discarded at this boundary: approval sent a fixed string and
/// the revision request sent a bare "please revise". These messages are deliberately written so
/// they are useful to <em>both</em> readers — the model gets the full plan plus every note as an
/// explicit instruction, and the user gets a readable audit record in the chat transcript, which is
/// also what makes the decision survive a session reload.
/// </remarks>
public static class PlanMessageComposer
{
    /// <summary>True when the reviewer supplied any feedback at all.</summary>
    public static bool HasUserInput(IReadOnlyList<PlanItem>? items, string? generalComment)
    {
        if (!string.IsNullOrWhiteSpace(generalComment)) return true;
        return items != null && items.Any(item => item != null && item.HasComment);
    }

    /// <summary>
    /// Message sent when the user approves the plan. Restates the approved steps and folds every
    /// reviewer note in as a binding requirement, so Act mode never has to re-read the plan out of
    /// a tool-call payload that may have been compacted out of context.
    /// </summary>
    public static string BuildApprovalMessage(IReadOnlyList<PlanItem>? items, string? generalComment, int revision)
    {
        var steps = Normalize(items);
        var builder = new StringBuilder();

        builder.Append("**Plan approved");
        if (revision > 1)
        {
            builder.Append(" (revision ").Append(revision.ToString(CultureInfo.InvariantCulture)).Append(')');
        }
        builder.AppendLine("** — switching to Act mode.");
        builder.AppendLine();

        if (steps.Count > 0)
        {
            builder.AppendLine("### Approved plan");
            AppendSteps(builder, steps, includeComments: true);
        }

        AppendGeneralComment(builder, generalComment, "Additional instructions from the reviewer");

        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine(
            "Implement the approved plan above, step by step, in the order listed. Reviewer notes and the " +
            "additional instructions are binding: where a note conflicts with the original step wording, the note " +
            "wins. Do not propose another plan and do not ask for approval again — start implementing now, and " +
            "state what you changed after each step.");

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Message sent when the user asks for changes. Lists only the steps that were commented on so
    /// the model can see exactly what to rework, then asks for a fresh plan tool call.
    /// </summary>
    public static string BuildRevisionRequestMessage(IReadOnlyList<PlanItem>? items, string? generalComment, int revision)
    {
        var steps = Normalize(items);
        var commented = steps.Where(step => step.HasComment).ToList();
        var builder = new StringBuilder();

        builder.Append("**Plan revision requested**");
        if (revision > 0)
        {
            builder.Append(" — feedback on revision ").Append(revision.ToString(CultureInfo.InvariantCulture));
        }
        builder.AppendLine(".");
        builder.AppendLine();

        if (commented.Count > 0)
        {
            builder.AppendLine("### Feedback on specific steps");
            foreach (var step in commented)
            {
                var index = steps.IndexOf(step) + 1;
                builder.Append("- **Step ").Append(index.ToString(CultureInfo.InvariantCulture)).Append(" — ")
                       .Append(Clean(step.Title)).AppendLine("**");
                builder.Append("  ").AppendLine(Indent(Clean(step.Comment)));
            }
            builder.AppendLine();
        }

        AppendGeneralComment(builder, generalComment, "Additional instructions from the reviewer");

        var uncommented = steps.Where(step => !step.HasComment).ToList();
        if (commented.Count > 0 && uncommented.Count > 0)
        {
            builder.AppendLine("Steps I did not comment on: " +
                string.Join(", ", uncommented.Select(step => $"\"{Clean(step.Title)}\"")) + ".");
            builder.AppendLine();
        }

        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine(
            "Revise the plan so it satisfies all of the feedback above. Keep the steps I did not comment on unless " +
            "the feedback forces them to change. Do not modify any files yet — when the revised plan is ready, call " +
            $"the `{AgentModePolicy.PlanToolName}` tool again with the complete updated list of steps.");

        return builder.ToString().TrimEnd();
    }

    /// <summary>Renders a plan as markdown, for display or for embedding in a message.</summary>
    public static string BuildPlanMarkdown(IReadOnlyList<PlanItem>? items, bool includeComments)
    {
        var steps = Normalize(items);
        if (steps.Count == 0) return string.Empty;

        var builder = new StringBuilder();
        AppendSteps(builder, steps, includeComments);
        return builder.ToString().TrimEnd();
    }

    private static void AppendSteps(StringBuilder builder, IReadOnlyList<PlanItem> steps, bool includeComments)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            builder.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(". **")
                   .Append(Clean(step.Title)).AppendLine("**");

            var description = Clean(step.Description);
            if (description.Length > 0)
            {
                builder.Append("   ").AppendLine(Indent(description));
            }

            if (includeComments && step.HasComment)
            {
                builder.Append("   - Reviewer note: ").AppendLine(Indent(Clean(step.Comment)));
            }
        }

        builder.AppendLine();
    }

    private static void AppendGeneralComment(StringBuilder builder, string? generalComment, string heading)
    {
        if (string.IsNullOrWhiteSpace(generalComment)) return;

        builder.Append("### ").AppendLine(heading);
        builder.AppendLine(Clean(generalComment));
        builder.AppendLine();
    }

    private static List<PlanItem> Normalize(IReadOnlyList<PlanItem>? items) =>
        items == null
            ? new List<PlanItem>()
            : items.Where(item => item != null).ToList();

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;

    /// <summary>
    /// Keeps multi-line reviewer text aligned under its markdown list item, so a comment with
    /// newlines does not break out of the list and change the structure the model sees.
    /// </summary>
    private static string Indent(string value) =>
        value.Replace("\r\n", "\n").Replace("\n", "\n   ");
}
