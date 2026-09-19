using System.Text;
using AiAssistant.Core.Models;

namespace AiAssistant.UI.Helpers;

public static class PlanMarkdownHelper
{
    public static string GenerateMarkdown(Plan plan)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# {plan.Title}");
        sb.AppendLine();
        sb.AppendLine($"**Status:** {plan.Status}");
        sb.AppendLine();
        
        if (!string.IsNullOrWhiteSpace(plan.Summary))
        {
            sb.AppendLine("## Summary");
            sb.AppendLine(plan.Summary);
            sb.AppendLine();
        }

        if (plan.ImportantChanges != null && plan.ImportantChanges.Count > 0)
        {
            sb.AppendLine("## Important Changes");
            foreach (var change in plan.ImportantChanges)
            {
                sb.AppendLine($"- {change}");
            }
            sb.AppendLine();
        }

        if (plan.ModifiedFiles != null && plan.ModifiedFiles.Count > 0)
        {
            sb.AppendLine("## Modified Files");
            foreach (var file in plan.ModifiedFiles)
            {
                sb.AppendLine($"- `[{file.Action.ToUpper()}]` {file.Path}: {file.Description}");
            }
            sb.AppendLine();
        }

        if (plan.Decisions != null && plan.Decisions.Count > 0)
        {
            sb.AppendLine("## Decisions");
            foreach (var decision in plan.Decisions)
            {
                sb.AppendLine($"- {decision}");
            }
            sb.AppendLine();
        }

        if (plan.Risks != null && plan.Risks.Count > 0)
        {
            sb.AppendLine("## Risks");
            foreach (var risk in plan.Risks)
            {
                sb.AppendLine($"- {risk}");
            }
            sb.AppendLine();
        }

        if (plan.OpenQuestions != null && plan.OpenQuestions.Count > 0)
        {
            sb.AppendLine("## Open Questions");
            foreach (var question in plan.OpenQuestions)
            {
                sb.AppendLine($"- {question.Question}");
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
