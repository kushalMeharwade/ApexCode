namespace AiAssistant.Core.Pipeline;

/// <summary>
/// INTENTIONALLY DISABLED — do not re-enable.
///
/// The ToolsSection previously emitted ~400 tokens of markdown prose listing enabled tools
/// on every request. This is pure waste: the model reads tool definitions from the JSON schema
/// in ChatOptions.Tools, not from markdown in the system prompt.
/// The DI registration in PipelineServiceCollectionExtensions has also been removed.
///
/// If mode-specific policy notes are ever needed beyond the JSON schema, add them as a
/// single-line comment in AgentModePolicy.BuildModeInstruction (which now runs in the
/// user-message volatile block, not the system prompt).
/// </summary>
[Obsolete("ToolsSection is disabled — tool definitions are provided via ChatOptions.Tools JSON schema.")]
internal sealed class ToolsSection : IPromptSection
{
    public int Order => int.MaxValue; // ensures it sorts last and can be filtered
    public bool ShouldRender(ContextState context) => false; // always disabled
    public string Render(ContextState context) => string.Empty;
}
