using AiAssistant.Core.Models;

namespace AiAssistant.Core.Services;

public interface IContextCompactor
{
    Task<string> CompactContextAsync(IEnumerable<ConversationTurn> turns, int maxTokens);
    Task<IEnumerable<ConversationTurn>> PruneHistoryAsync(IEnumerable<ConversationTurn> turns, int maxTokens);
    Task<string> SummarizeConversationAsync(IEnumerable<ConversationTurn> turns);
    int EstimateTokensForTurn(ConversationTurn turn);
}
