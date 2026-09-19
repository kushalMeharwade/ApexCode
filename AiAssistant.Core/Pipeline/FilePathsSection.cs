using System.Text;

namespace AiAssistant.Core.Pipeline;

public sealed class FilePathsSection : IPromptSection
{
    // Render after EnvironmentSection
    public int Order => 210;
    
    public bool ShouldRender(ContextState context) => true;

    public string Render(ContextState context)
    {
        var section = new StringBuilder("## File Paths")
            .AppendLine()
            .AppendLine("- The workspace root is configured automatically.")
            .AppendLine("- In ALL tool calls, use paths RELATIVE to the workspace root.")
            .AppendLine("- Always use forward slashes, e.g.: src/app/pages/file.html")
            .AppendLine("- NEVER include drive letters or the workspace root in tool-call paths.")
            .AppendLine("- If you are unsure of a file's location, use search_codebase first.")
            .AppendLine("- The prefix 'GITHUB_LATEST_CODE' is invalid. Do not use it.");

        return section.ToString();
    }
}
