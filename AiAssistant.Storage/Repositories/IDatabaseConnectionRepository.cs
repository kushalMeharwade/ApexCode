using AiAssistant.Storage.Models;

namespace AiAssistant.Storage.Repositories;

public interface IDatabaseConnectionRepository
{
    Task<DatabaseConnection?> GetByIdAsync(string id);
    Task<IEnumerable<DatabaseConnection>> GetAllAsync();
    Task<DatabaseConnection?> GetByNameAsync(string name);
    Task<DatabaseConnection> AddAsync(DatabaseConnection connection);
    Task<DatabaseConnection> UpdateAsync(DatabaseConnection connection);
    Task DeleteAsync(string id);
}
