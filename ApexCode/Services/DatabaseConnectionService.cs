using System.Linq;
using AiAssistant.Core.Services;
using AiAssistant.Storage.Models;
using AiAssistant.Storage.Repositories;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace ApexCode.Services;

/// <summary>
/// Service for managing database connections, schema reading, and query execution.
/// </summary>
public class DatabaseConnectionService : IDatabaseConnectionService
{
    private readonly IDatabaseConnectionRepository _repository;
    private readonly ILogger<DatabaseConnectionService> _logger;
    private DatabaseConnection? _activeConnection;

    public DatabaseConnection? ActiveConnection => _activeConnection;

    public DatabaseConnectionService(
        IDatabaseConnectionRepository repository,
        ILogger<DatabaseConnectionService> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<DatabaseConnection>> GetAllConnectionsAsync()
    {
        return (await _repository.GetAllAsync()).ToList();
    }

    public async Task<DatabaseConnection?> GetConnectionAsync(string id)
    {
        return await _repository.GetByIdAsync(id);
    }

    public async Task<DatabaseConnection> SaveConnectionAsync(DatabaseConnection connection)
    {
        var existing = await _repository.GetByIdAsync(connection.Id);
        if (existing != null)
            return await _repository.UpdateAsync(connection);
        return await _repository.AddAsync(connection);
    }

    public async Task DeleteConnectionAsync(string id)
    {
        if (_activeConnection?.Id == id)
            _activeConnection = null;
        await _repository.DeleteAsync(id);
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(DatabaseConnection connection)
    {
        try
        {
            var connectionString = BuildConnectionString(connection);
            using var sqlConnection = await ApexCode.Helpers.DatabaseConnectionHelper.OpenConnectionAsync(connectionString, _logger);
            return (true, "Connection successful!");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connection test failed for {ConnectionName}", connection.Name);
            return (false, ex.Message);
        }
    }

    public Task<string> GetActiveConnectionStringAsync()
    {
        if (_activeConnection == null)
            return Task.FromResult(string.Empty);
        return Task.FromResult(BuildConnectionString(_activeConnection));
    }

    public Task<string> GetConnectionStringAsync(DatabaseConnection connection)
    {
        if (connection == null) throw new ArgumentNullException(nameof(connection));
        return Task.FromResult(BuildConnectionString(connection));
    }

    public async Task SetActiveConnectionAsync(string connectionId)
    {
        _activeConnection = await _repository.GetByIdAsync(connectionId);
    }

    public void ClearActiveConnection()
    {
        _activeConnection = null;
    }

    private static string BuildConnectionString(DatabaseConnection connection)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = connection.ServerAddress,
            InitialCatalog = connection.DatabaseName,
            IntegratedSecurity = connection.Authentication == AuthenticationType.WindowsAuth,
            TrustServerCertificate = true
        };

        if (connection.Authentication == AuthenticationType.SqlAuth)
        {
            builder.UserID = connection.Username ?? "";
            var password = connection.DecryptPassword();
            if (password == null)
            {
                throw new InvalidOperationException("Failed to decrypt database password. Please re-enter your credentials.");
            }
            builder.Password = password;
        }

        builder.TrustServerCertificate = true;
        builder.ConnectTimeout = 10;
        return builder.ConnectionString;
    }
}

