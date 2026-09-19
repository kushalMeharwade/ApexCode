using AiAssistant.Core.Models;
using AiAssistant.Storage.Models;

namespace AiAssistant.Core.Services;

public interface ISessionManager
{
    Task<Session> CreateSessionAsync(string name, string providerId, string modelId, string systemPromptId);
    Task<Session?> GetSessionAsync(string id);
    Task<IEnumerable<Session>> GetAllSessionsAsync();
    Task<IEnumerable<SessionSummary>> GetSessionSummariesAsync();
    Task<Session> UpdateSessionAsync(Session session);
    Task DeleteSessionAsync(string id);
    Task<Session?> GetActiveSessionAsync();
    Task SetActiveSessionAsync(string id);
    Task AddMessageAsync(string sessionId, ConversationTurn turn);
    Task<IEnumerable<ConversationTurn>> GetConversationHistoryAsync(string sessionId);
    /// <summary>Deletes all messages for a session without deleting the session itself.</summary>
    Task DeleteSessionMessagesAsync(string sessionId);
    /// <summary>Atomically removes the selected message, later messages and their checkpoints.</summary>
    Task RevertMessagesAsync(string sessionId, string messageId);
}
