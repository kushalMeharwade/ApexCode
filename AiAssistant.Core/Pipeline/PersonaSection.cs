using System.Text;

namespace AiAssistant.Core.Pipeline;

public sealed class PersonaSection : IPromptSection
{
    public int Order => 100;
    public bool ShouldRender(ContextState context) => context.ActivePersona is not null;

    public string Render(ContextState context)
    {
        var persona = context.ActivePersona!;
        return new StringBuilder("# Persona")
            .AppendLine()
            .AppendLine($"- **Role:** {persona.Role}")
            .AppendLine($"- **Tone:** {persona.Tone}")
            .AppendLine($"- **Expertise:** {persona.Expertise}")
            .AppendLine()
            .AppendLine("## Instructions")
            .Append(persona.Instructions)
            .ToString();
    }
}
