using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AiAssistant.Core.Models;
using AiAssistant.Storage.Models;
using AiAssistant.Storage.Repositories;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Core.Services;

public class SessionManager : ISessionManager
{
    private readonly ISessionRepository _sessionRepository;
    private readonly IMessageRepository _messageRepository;
    private readonly ILogger<SessionManager> _logger;

    public SessionManager(
        ISessionRepository sessionRepository,
        IMessageRepository messageRepository,
        ILogger<SessionManager> logger)
    {
        _sessionRepository = sessionRepository;
        _messageRepository = messageRepository;
        _logger = logger;
    }

    public async Task<Session> CreateSessionAsync(string name, string providerId, string modelId, string systemPromptId)
    {
        var session = new Session
        {
            Name = name,
            ProviderId = providerId,
            ModelId = modelId,
            SystemPromptId = systemPromptId,
            IsActive = false
        };
        return await _sessionRepository.AddAsync(session);
    }

    public async Task<Session?> GetSessionAsync(string id)
    {
        return await _sessionRepository.GetByIdAsync(id);
    }

    public async Task<IEnumerable<Session>> GetAllSessionsAsync()
    {
        return await _sessionRepository.GetAllAsync();
    }

    public Task<IEnumerable<SessionSummary>> GetSessionSummariesAsync()
    {
        return _sessionRepository.GetSummariesAsync();
    }

    public async Task<Session> UpdateSessionAsync(Session session)
    {
        return await _sessionRepository.UpdateAsync(session);
    }

    public async Task DeleteSessionAsync(string id)
    {
        await _sessionRepository.DeleteAsync(id);
    }

    public async Task<Session?> GetActiveSessionAsync()
    {
        return await _sessionRepository.GetActiveAsync();
    }

    public async Task SetActiveSessionAsync(string id)
    {
        await _sessionRepository.SetActiveAsync(id);
    }

    public async Task AddMessageAsync(string sessionId, ConversationTurn turn)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));
        if (turn == null)
            throw new ArgumentNullException(nameof(turn));

        var message = new Message
        {
            Id = turn.Id,
            SessionId = sessionId,
            Role = turn.Role,
            Content = turn.Content,
            Timestamp = turn.Timestamp,
            SerializedContentBlocks = turn.SerializedContentBlocks,
            OriginalPrompt = turn.OriginalPrompt,
            IsStreaming = turn.Metadata?.ContainsKey("isStreaming") == true && turn.Metadata["isStreaming"] is bool isStreaming && isStreaming,
            Metadata = turn.Metadata != null ? System.Text.Json.JsonSerializer.Serialize(turn.Metadata) : null
        };
        await _messageRepository.AddAsync(message);
        var session = await _sessionRepository.GetByIdAsync(sessionId);
        if (session != null)
        {
            await _sessionRepository.UpdateAsync(session with { UpdatedAt = DateTime.UtcNow });
        }
    }

    public async Task<IEnumerable<ConversationTurn>> GetConversationHistoryAsync(string sessionId)
    {
        var messages = await _messageRepository.GetBySessionIdAsync(sessionId);
        return messages.Select(m => new ConversationTurn
        {
            Id = m.Id,
            Role = m.Role,
            Content = m.Content,
            SerializedContentBlocks = m.SerializedContentBlocks,
            OriginalPrompt = m.OriginalPrompt,
            Timestamp = m.Timestamp,
            Metadata = !string.IsNullOrEmpty(m.Metadata) 
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(m.Metadata!) ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        }).ToList();
    }

    public async Task DeleteSessionMessagesAsync(string sessionId)
    {
        await _messageRepository.DeleteBySessionIdAsync(sessionId);
        var session = await _sessionRepository.GetByIdAsync(sessionId);
        if (session != null)
            await _sessionRepository.UpdateAsync(session with { UpdatedAt = DateTime.UtcNow });
    }

    public Task RevertMessagesAsync(string sessionId, string messageId)
        => _messageRepository.DeleteFromMessageAsync(sessionId, messageId);
}
