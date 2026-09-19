namespace AiAssistant.Core.Pipeline;

public sealed class ContextStateBuilder
{
    public string OsPlatform { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    public DateTime UtcNow { get; set; } = DateTime.UtcNow;
    public string UserTimeZone { get; set; } = TimeZoneInfo.Local.Id;
    public string? ActiveWorkspacePath { get; set; }
    public IList<string> OpenFiles { get; } = new List<string>();
    public IDictionary<string, string> UserSettings { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public PersonaContext? ActivePersona { get; set; }
    public IList<ToolContext> EnabledTools { get; } = new List<ToolContext>();

    public ContextState Build() => new(
        OsPlatform,
        AppVersion,
        UtcNow,
        UserTimeZone,
        ActiveWorkspacePath,
        OpenFiles.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
        new Dictionary<string, string>(UserSettings, StringComparer.OrdinalIgnoreCase),
        ActivePersona,
        EnabledTools.GroupBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase).ToArray());
}
