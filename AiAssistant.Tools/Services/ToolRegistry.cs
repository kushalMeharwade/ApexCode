using Microsoft.Extensions.AI;
using System.Threading;
using System.Threading.Tasks;

namespace AiAssistant.Tools.Services;

public class ToolRegistry : IToolRegistry
{
    private readonly List<AIFunction> _functions = new();

    public IReadOnlyList<AIFunction> GetTools() => _functions;

    public void RegisterFunction(AIFunction function)
    {
        _functions.Add(function);
    }

    public void RegisterToolProvider(IToolProvider provider)
    {
        RegisterFunction(provider.CreateFunction());
    }

    public Task CancelAllAsync(CancellationToken ct = default)
    {
        // ToolRegistry holds stateless AIFunction registrations.
        // Individual tools manage their own cancellation via the CancellationToken
        // passed to each invocation — nothing to cancel here at the registry level.
        return Task.CompletedTask;
    }
}
