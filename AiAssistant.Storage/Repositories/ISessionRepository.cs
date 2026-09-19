using AiAssistant.Storage.Models;

namespace AiAssistant.Storage.Repositories;

public interface ISessionRepository
{
    Task<Session?> GetByIdAsync(string id);
    Task<IEnumerable<Session>> GetAllAsync();
    Task<IEnumerable<SessionSummary>> GetSummariesAsync();
    Task<Session> AddAsync(Session session);
    Task<Session> UpdateAsync(Session session);
    Task DeleteAsync(string id);
    Task<Session?> GetActiveAsync();
    Task SetActiveAsync(string id);
}
