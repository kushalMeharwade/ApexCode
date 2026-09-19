using System.Security.Cryptography;
using System.Text;

namespace AiAssistant.Core.Pipeline;

public sealed record PersonaContext(
    string Id,
    string Role,
    string Tone,
    string Expertise,
    string Instructions);

public sealed record ToolContext(string Name, string Description);

public sealed record ContextState(
    string OsPlatform,
    string AppVersion,
    DateTime UtcNow,
    string UserTimeZone,
    string? ActiveWorkspacePath,
    IReadOnlyList<string> OpenFiles,
    IReadOnlyDictionary<string, string> UserSettings,
    PersonaContext? ActivePersona,
    IReadOnlyList<ToolContext> EnabledTools)
{
    /// <summary>
    /// The file path currently active in the editor, if any.
    /// Volatile: injected into the user-message context block, not the system prompt.
    /// </summary>
    public string? ActiveFilePath { get; init; }

    /// <summary>
    /// The editor caret line (1-indexed), if available.
    /// Volatile: injected into the user-message context block, not the system prompt.
    /// </summary>
    public int? CaretLine { get; init; }

    public string StableHash => ComputeStableHash();


    private string ComputeStableHash()
    {
        // Only hash values that are invariant for the lifetime of a session.
        // UtcNow and OpenFiles are intentionally excluded: they change every turn / tab switch
        // and would defeat provider-side prefix caching (needs byte-identical prefix ≥1024 tokens).
        // Volatile context (timestamp, open files, mode) is injected into the user message instead.
        var stable = new StringBuilder()
            .AppendLine(OsPlatform)
            .AppendLine(AppVersion)
            .AppendLine(UserTimeZone)
            .AppendLine(ActiveWorkspacePath ?? string.Empty);

        AppendSorted(stable, UserSettings.Select(pair => $"{pair.Key}={pair.Value}"));
        if (ActivePersona is not null)
            stable.AppendLine($"{ActivePersona.Id}|{ActivePersona.Role}|{ActivePersona.Tone}|{ActivePersona.Expertise}|{ActivePersona.Instructions}");
        AppendSorted(stable, EnabledTools.Select(tool => $"{tool.Name}|{tool.Description}"));

        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(stable.ToString()));
        return BitConverter.ToString(bytes).Replace("-", string.Empty);
    }

    private static void AppendSorted(StringBuilder builder, IEnumerable<string> values)
    {
        foreach (var value in values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            builder.AppendLine(value);
    }
}
