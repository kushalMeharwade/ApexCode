using System;
using System.Text;
using AiAssistant.Core.Models;

namespace AiAssistant.Core.Services;

/// <summary>
/// Handles bidirectional parsing and rendering of a Plan object to/from Markdown.
/// </summary>
public class PlanMarkdownRenderer
{
    public string Render(Plan plan)
    {
        var sb = new StringBuilder();

        // Frontmatter
        sb.AppendLine("---");
        sb.AppendLine($"id: {plan.Id}");
        sb.AppendLine($"status: {plan.Status}");
        sb.AppendLine($"created_at: {plan.CreatedAtUtc:O}");
        sb.AppendLine("---");
        sb.AppendLine();

        // Title
        sb.AppendLine($"# {plan.Title}");
        sb.AppendLine();

        // Summary
        sb.AppendLine("## Summary");
        sb.AppendLine(plan.Summary);
        sb.AppendLine();

        // Important Changes
        if (plan.ImportantChanges.Count > 0)
        {
            sb.AppendLine("## Important Changes");
            foreach (var change in plan.ImportantChanges)
            {
                sb.AppendLine($"- {change}");
            }
            sb.AppendLine();
        }

        // Modified Files
        if (plan.ModifiedFiles.Count > 0)
        {
            sb.AppendLine("## Modified Files");
            foreach (var file in plan.ModifiedFiles)
            {
                sb.AppendLine($"- **[{file.Action}]** `{file.Path}`: {file.Description}");
            }
            sb.AppendLine();
        }

        // Decisions
        if (plan.Decisions.Count > 0)
        {
            sb.AppendLine("## Decisions");
            foreach (var decision in plan.Decisions)
            {
                sb.AppendLine($"- {decision}");
            }
            sb.AppendLine();
        }

        // Risks
        if (plan.Risks.Count > 0)
        {
            sb.AppendLine("## Risks");
            foreach (var risk in plan.Risks)
            {
                sb.AppendLine($"- {risk}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public Plan Parse(string markdown)
    {
        // For the sake of the skeleton, returning a new plan.
        // A complete implementation would parse the frontmatter (YamlDotNet)
        // and extract sections based on markdown headers.
        
        var plan = new Plan
        {
            Title = "Parsed Plan",
            Summary = "Parsed summary placeholder"
        };
        
        return plan;
    }
}
