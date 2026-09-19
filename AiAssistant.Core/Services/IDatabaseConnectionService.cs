using AiAssistant.Storage.Models;

namespace AiAssistant.Core.Services;

/// <summary>
/// Service for managing database connections, schema reading, and query execution.
/// Implemented at the VSIX layer where SqlClient and DI are available.
/// </summary>
public interface IDatabaseConnectionService
{
    Task<IReadOnlyList<DatabaseConnection>> GetAllConnectionsAsync();
    Task<DatabaseConnection?> GetConnectionAsync(string id);
    Task<DatabaseConnection> SaveConnectionAsync(DatabaseConnection connection);
    Task DeleteConnectionAsync(string id);
    Task<(bool Success, string Message)> TestConnectionAsync(DatabaseConnection connection);
    Task<string> GetActiveConnectionStringAsync();
    /// <summary>Resolve the captured connection, never a subsequently selected active connection.</summary>
    Task<string> GetConnectionStringAsync(DatabaseConnection connection);
    DatabaseConnection? ActiveConnection { get; }
    Task SetActiveConnectionAsync(string connectionId);
    void ClearActiveConnection();
}
