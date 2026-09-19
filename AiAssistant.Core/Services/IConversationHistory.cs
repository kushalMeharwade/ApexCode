using AiAssistant.Core.Models;

namespace AiAssistant.Core.Services;

public interface IConversationHistory
{
    Task AddTurnAsync(string sessionId, ConversationTurn turn);
    Task<IEnumerable<ConversationTurn>> GetHistoryAsync(string sessionId);
    Task ClearHistoryAsync(string sessionId);
    Task<int> GetTokenCountAsync(string sessionId);
}
