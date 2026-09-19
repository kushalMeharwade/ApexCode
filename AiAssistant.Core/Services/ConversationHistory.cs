using AiAssistant.Core.Models;
using AiAssistant.Storage.Repositories;

namespace AiAssistant.Core.Services;

public class ConversationHistory : IConversationHistory
{
    private readonly ISessionManager _sessionManager;
    private volatile bool _isClearing;

    public ConversationHistory(ISessionManager sessionManager)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
    }

    public async Task AddTurnAsync(string sessionId, ConversationTurn turn)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));
        if (turn == null)
            throw new ArgumentNullException(nameof(turn));

        await _sessionManager.AddMessageAsync(sessionId, turn);
    }

    public async Task<IEnumerable<ConversationTurn>> GetHistoryAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

        return await _sessionManager.GetConversationHistoryAsync(sessionId);
    }

    public async Task ClearHistoryAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

        if (_isClearing) return;
        try
        {
            _isClearing = true;
            // FIX: Delete all messages for the session without deleting the session itself.
            // The previous implementation deleted the session and created a new one, giving it a
            // brand-new GUID. Any caller (ChatService.ActiveSession, UI) that still held the old
            // session ID would then hit a foreign-key / not-found error on the next send.
            // MessageRepository.DeleteBySessionIdAsync removes only the message rows, keeping the
            // session row and its ID stable — callers never see a stale reference.
            await _sessionManager.DeleteSessionMessagesAsync(sessionId);
        }
        finally
        {
            _isClearing = false;
        }
    }

    public async Task<int> GetTokenCountAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

        var history = await _sessionManager.GetConversationHistoryAsync(sessionId);
        // FIX: Use ContextCompactor.EstimateTokens (TiktokenTokenizer-backed, 3.5 chars/token
        // heuristic fallback) instead of the raw `Length / 4` approximation, and account for
        // SerializedContentBlocks so tool-call turns are counted correctly.
        return history.Sum(t => AiAssistant.Core.Services.ContextCompactor.EstimateTokensForTurnStatic(t));
    }
}
