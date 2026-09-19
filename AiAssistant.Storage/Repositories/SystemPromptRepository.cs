using AiAssistant.Storage.Database;
using AiAssistant.Storage.Models;
using Dapper;
using System.Data.SQLite;
using System.Data;

namespace AiAssistant.Storage.Repositories;

public class SystemPromptRepository : ISystemPromptRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public SystemPromptRepository(IDbConnectionFactory connectionFactory)
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

    public async Task<SystemPrompt?> GetByIdAsync(string id)
    {
        using var connection = await GetOpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<SystemPrompt>(
            "SELECT * FROM system_prompts WHERE id = @Id", new { Id = id });
    }

    public async Task<IEnumerable<SystemPrompt>> GetAllAsync()
    {
        using var connection = await GetOpenConnectionAsync();
        return await connection.QueryAsync<SystemPrompt>("SELECT * FROM system_prompts ORDER BY is_default DESC, name");
    }

    public async Task<SystemPrompt?> GetByNameAsync(string name)
    {
        using var connection = await GetOpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<SystemPrompt>(
            "SELECT * FROM system_prompts WHERE name = @Name", new { Name = name });
    }

    public async Task<SystemPrompt?> GetDefaultAsync()
    {
        using var connection = await GetOpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<SystemPrompt>(
            "SELECT * FROM system_prompts WHERE is_default = 1");
    }

    public async Task<SystemPrompt> AddAsync(SystemPrompt prompt)
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            @"INSERT INTO system_prompts (id, name, content, variables, is_default, created_at, updated_at) 
              VALUES (@Id, @Name, @Content, @Variables, @IsDefault, @CreatedAt, @UpdatedAt)",
            prompt);
        return prompt;
    }

    public async Task<SystemPrompt> UpdateAsync(SystemPrompt prompt)
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            @"UPDATE system_prompts SET name = @Name, content = @Content, variables = @Variables, 
              is_default = @IsDefault, updated_at = @UpdatedAt WHERE id = @Id",
            prompt);
        return prompt;
    }

    public async Task DeleteAsync(string id)
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync("DELETE FROM system_prompts WHERE id = @Id", new { Id = id });
    }

    public async Task SetDefaultAsync(string id)
    {
        using var connection = await GetOpenConnectionAsync();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync("UPDATE system_prompts SET is_default = 0", transaction: transaction);
        await connection.ExecuteAsync("UPDATE system_prompts SET is_default = 1 WHERE id = @Id", new { Id = id }, transaction: transaction);
        transaction.Commit();
    }
}
