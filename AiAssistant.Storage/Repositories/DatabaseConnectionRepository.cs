using AiAssistant.Storage.Database;
using AiAssistant.Storage.Models;
using Dapper;
using System.Data.SQLite;
using System.Data;

namespace AiAssistant.Storage.Repositories;

public class DatabaseConnectionRepository : IDatabaseConnectionRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public DatabaseConnectionRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    private async Task<SQLiteConnection> GetOpenConnectionAsync()
    {
        var connection = await _connectionFactory.CreateConnectionAsync();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync();
        return connection;
    }

    public async Task<DatabaseConnection?> GetByIdAsync(string id)
    {
        try
        {
            using var connection = await GetOpenConnectionAsync();
            return await connection.QuerySingleOrDefaultAsync<DatabaseConnection>(
                "SELECT *, authentication_type AS Authentication, permission_level AS Permission FROM database_connections WHERE id = @Id", new { Id = id });
        }
        catch (SQLiteException ex) when (ex.ErrorCode == 1) // no such table
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] Table database_connections not found in GetByIdAsync. Auto-initializing schema...");
            await AiAssistantDbContext.EnsureInitializedAsync(_connectionFactory);

            using var retryConn = await GetOpenConnectionAsync();
            return await retryConn.QuerySingleOrDefaultAsync<DatabaseConnection>(
                "SELECT *, authentication_type AS Authentication, permission_level AS Permission FROM database_connections WHERE id = @Id", new { Id = id });
        }
    }

    public async Task<IEnumerable<DatabaseConnection>> GetAllAsync()
    {
        try
        {
            using var connection = await GetOpenConnectionAsync();
            return await connection.QueryAsync<DatabaseConnection>(
                "SELECT *, authentication_type AS Authentication, permission_level AS Permission FROM database_connections ORDER BY name");
        }
        catch (SQLiteException ex) when (ex.ErrorCode == 1) // no such table
        {
            string dataSource = "Unknown";
            string tablesList = "Unknown";
            try
            {
                using var connection = await GetOpenConnectionAsync();
                dataSource = connection.DataSource;
                var tables = await connection.QueryAsync<string>("SELECT name FROM sqlite_schema WHERE type='table'");
                tablesList = string.Join(", ", tables);
            }
            catch (Exception innerEx)
            {
                tablesList = $"Failed to get tables: {innerEx.Message}";
            }
            
            throw new Exception($"Failed to load DB. Path: {dataSource}. Tables: {tablesList}. Original error: {ex.Message}", ex);
        }
    }

    public async Task<DatabaseConnection?> GetByNameAsync(string name)
    {
        try
        {
            using var connection = await GetOpenConnectionAsync();
            return await connection.QuerySingleOrDefaultAsync<DatabaseConnection>(
                "SELECT *, authentication_type AS Authentication, permission_level AS Permission FROM database_connections WHERE name = @Name", new { Name = name });
        }
        catch (SQLiteException ex) when (ex.ErrorCode == 1) // no such table
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] Table database_connections not found in GetByNameAsync. Auto-initializing schema...");
            await AiAssistantDbContext.EnsureInitializedAsync(_connectionFactory);

            using var retryConn = await GetOpenConnectionAsync();
            return await retryConn.QuerySingleOrDefaultAsync<DatabaseConnection>(
                "SELECT *, authentication_type AS Authentication, permission_level AS Permission FROM database_connections WHERE name = @Name", new { Name = name });
        }
    }

    public async Task<DatabaseConnection> AddAsync(DatabaseConnection connection)
    {
        using var dbConnection = await GetOpenConnectionAsync();
        await dbConnection.ExecuteAsync(
            @"INSERT INTO database_connections (id, name, server_address, database_name, authentication_type, username, encrypted_password, is_enabled, permission_level, require_approval, created_at)
              VALUES (@Id, @Name, @ServerAddress, @DatabaseName, @Authentication, @Username, @EncryptedPassword, @IsEnabled, @Permission, @RequireApproval, @CreatedAt)",
            connection);
        return connection;
    }

    public async Task<DatabaseConnection> UpdateAsync(DatabaseConnection connection)
    {
        using var dbConnection = await GetOpenConnectionAsync();
        await dbConnection.ExecuteAsync(
            @"UPDATE database_connections SET name = @Name, server_address = @ServerAddress, database_name = @DatabaseName,
              authentication_type = @Authentication, username = @Username, encrypted_password = @EncryptedPassword,
              is_enabled = @IsEnabled, permission_level = @Permission, require_approval = @RequireApproval
              WHERE id = @Id",
            connection);
        return connection;
    }

    public async Task DeleteAsync(string id)
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM database_connections WHERE id = @Id", new { Id = id });
    }
}
