using AiAssistant.Core.Models;
using Microsoft.Extensions.AI;
using ChatMessage = AiAssistant.Core.Models.ChatMessage;

namespace AiAssistant.Core.Pipeline;

public interface IProviderAdapter
{
    string ProviderName { get; }
    object BuildRequest(
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        string userMessage,
        ChatOptions options);
}
