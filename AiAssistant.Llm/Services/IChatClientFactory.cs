using System;
using Microsoft.Extensions.AI;

namespace AiAssistant.Llm.Services;

public record LlmRetryInitiated(TimeSpan Delay, string Reason);

public interface IChatClientFactory
{
    event EventHandler<LlmRetryInitiated>? OnRetry;
    IChatClient CreateClient(string providerId, string modelId, string? apiKey = null, string? apiEndpoint = null);
    IChatClient GetDefaultClient();
    void RegisterProvider(string providerId, Func<string, string, string?, IChatClient> factory);
    void UnregisterProvider(string providerId);
    IChatClient CreateClientWithTools(string providerId, string modelId, string? apiKey = null, string? apiEndpoint = null, IList<AITool>? tools = null);
    void InvalidateCache();
}
