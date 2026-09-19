using System.Text;

namespace AiAssistant.Core.Pipeline;

public sealed class SystemPromptBuilder(IEnumerable<IPromptSection> sections)
{
    private readonly IReadOnlyList<IPromptSection> _sections = sections
        .OrderBy(section => section.Order)
        .ToArray();

    public string Build(ContextState context)
    {
        if (context is null)
            throw new ArgumentNullException(nameof(context));
        var prompt = new StringBuilder();

        foreach (var section in _sections.Where(section => section.ShouldRender(context)))
        {
            var rendered = section.Render(context).Trim();
            if (rendered.Length == 0)
                continue;

            if (prompt.Length > 0)
                prompt.AppendLine().AppendLine();
            prompt.Append(rendered);
        }

        return prompt.ToString();
    }
}
