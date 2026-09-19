using System.Text;

namespace AiAssistant.Core.Pipeline;

public sealed class EnvironmentSection : IPromptSection
{
    public int Order => 200;
    public bool ShouldRender(ContextState context) => true;

    public string Render(ContextState context)
    {
        // NOTE: UtcNow and OpenFiles are intentionally omitted here.
        // They change on every turn / tab switch and would mutate the system prompt hash,
        // defeating provider-side prefix caching. They are injected into the volatile
        // context block prepended to the user message by ChatRequestPipeline instead.
        var section = new StringBuilder("# Environment")
            .AppendLine()
            .AppendLine($"- **OS:** {context.OsPlatform}")
            .AppendLine($"- **App version:** {context.AppVersion}")
            .AppendLine($"- **Time zone:** {context.UserTimeZone}")
            .AppendLine($"- **Workspace:** {context.ActiveWorkspacePath ?? "None"}");

        return section.ToString();
    }
}
