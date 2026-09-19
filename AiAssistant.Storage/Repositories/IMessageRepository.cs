using AiAssistant.Storage.Models;

namespace AiAssistant.Storage.Repositories;

public interface IMessageRepository
{
    Task<Message?> GetByIdAsync(string id);
    Task<IEnumerable<Message>> GetBySessionIdAsync(string sessionId);
    Task<Message> AddAsync(Message message);
    Task<Message> UpdateAsync(Message message);
    Task DeleteAsync(string id);
    Task DeleteBySessionIdAsync(string sessionId);
    Task DeleteFromMessageAsync(string sessionId, string messageId);
}
