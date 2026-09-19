using AiAssistant.Core.Models;
using Microsoft.Extensions.AI;
using ChatMessage = AiAssistant.Core.Models.ChatMessage;

namespace AiAssistant.Core.Pipeline;

public interface IChatRequestPipeline
{
    Task<PreparedChatRequest> SendAsync(
        string providerType,
        IReadOnlyList<ChatMessage> history,
        string userMessage,
        ChatOptions options,
        string activeMode,
        int fileContextBudgetChars,
        IDictionary<string, object?>? currentTurnMetadata = null,
        CancellationToken cancellationToken = default);
}
