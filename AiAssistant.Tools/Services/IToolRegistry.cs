using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace AiAssistant.Tools.Services;

public interface IToolRegistry
{
    IReadOnlyList<AIFunction> GetTools();
    void RegisterFunction(AIFunction function);
    void RegisterToolProvider(IToolProvider provider);
    Task CancelAllAsync(CancellationToken ct);
}
