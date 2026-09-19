using System.Text.Json;
using AiAssistant.Core.Pipeline;
using AiAssistant.Core.Services;
using AiAssistant.Storage.Models;

namespace ApexCode.Pipeline;

public sealed class PersonaContextCollector(IPersonaService personas) : IContextCollector
{
    public async Task CollectAsync(ContextStateBuilder builder, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var persona = personas.ActivePersona;
        if (persona is null)
            persona = (await personas.GetAllPersonasAsync().ConfigureAwait(false)).FirstOrDefault(item => item.IsDefault);

        builder.ActivePersona = persona is null
            ? new PersonaContext("default-assistant", "AI coding assistant", "Professional and concise", "Software engineering", "Help developers write, review, and debug readable code.")
            : Map(persona);
    }

    private static PersonaContext Map(SystemPrompt persona) => new(
        persona.Id,
        ReadVariable(persona.Variables, "role") ?? persona.Name,
        ReadVariable(persona.Variables, "tone") ?? "Professional and concise",
        ReadVariable(persona.Variables, "expertise") ?? "Software engineering",
        persona.Content);

    private static string? ReadVariable(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return property.Value.GetString();
        }
        catch (JsonException) { }
        return null;
    }
}
