using System.Collections.Generic;
using System.Threading.Tasks;
using AiAssistant.Storage.Models;

namespace AiAssistant.Storage.Repositories;

public interface ICheckpointRepository
{
    Task SaveAsync(Checkpoint checkpoint);
    Task<Checkpoint?> GetByIdAsync(string id);
    Task<Checkpoint?> GetByMessageIdAsync(string messageId);
    Task<IEnumerable<Checkpoint>> GetBySessionIdAsync(string sessionId);
    Task DeleteBySessionIdAsync(string sessionId);
}
