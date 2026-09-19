namespace AiAssistant.Core.Pipeline;

public interface IContextCollector
{
    Task CollectAsync(ContextStateBuilder builder, CancellationToken cancellationToken = default);
}
