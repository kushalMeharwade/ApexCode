namespace AiAssistant.Core.Pipeline;

public interface IPromptSection
{
    int Order { get; }
    bool ShouldRender(ContextState context);
    string Render(ContextState context);
}
