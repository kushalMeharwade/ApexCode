using AiAssistant.Core.Pipeline;
using AiAssistant.Tools.Services;

namespace ApexCode.Pipeline;

public sealed class ToolContextCollector(IToolRegistry tools) : IContextCollector
{
    public Task CollectAsync(ContextStateBuilder builder, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var tool in tools.GetTools())
            builder.EnabledTools.Add(new ToolContext(tool.Name, tool.Description ?? string.Empty));
        return Task.CompletedTask;
    }
}
