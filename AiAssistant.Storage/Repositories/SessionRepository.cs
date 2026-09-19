using AiAssistant.Storage.Database;
using AiAssistant.Storage.Models;
using Dapper;
using System.Data.SQLite;
using System.Data;

namespace AiAssistant.Storage.Repositories;

public class SessionRepository : ISessionRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public SessionRepository(IDbConnectionFactory connectionFactory)
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

    public async Task<Session?> GetByIdAsync(string id)
    {
        using var connection = await GetOpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<Session>(
            "SELECT * FROM sessions WHERE id = @Id", new { Id = id });
    }

    public async Task<IEnumerable<Session>> GetAllAsync()
    {
        using var connection = await GetOpenConnectionAsync();
        return await connection.QueryAsync<Session>("SELECT * FROM sessions ORDER BY updated_at DESC");
    }

    public async Task<IEnumerable<SessionSummary>> GetSummariesAsync()
    {
        using var connection = await GetOpenConnectionAsync();
        // The indexed lookup stops at the first user message. Other message
        // payloads and metadata are never materialized for the history list.
        return await connection.QueryAsync<SessionSummary>(@"
            SELECT s.id AS Id, s.name AS Name, s.updated_at AS UpdatedAt,
                   COALESCE(NULLIF(m.original_prompt, ''), m.content) AS FirstPrompt
            FROM sessions s
            LEFT JOIN messages m ON m.rowid = (
                SELECT first.rowid FROM messages first
                WHERE first.session_id = s.id AND first.role = 'user'
                ORDER BY first.timestamp, first.rowid LIMIT 1
            )
            ORDER BY s.updated_at DESC");
    }

    public async Task<Session> AddAsync(Session session)
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            @"INSERT INTO sessions (id, name, created_at, updated_at, provider_id, model_id, system_prompt_id, is_active, active_mode) 
              VALUES (@Id, @Name, @CreatedAt, @UpdatedAt, @ProviderId, @ModelId, @SystemPromptId, @IsActive, @ActiveMode)",
            session);
        return session;
    }

    public async Task<Session> UpdateAsync(Session session)
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            @"UPDATE sessions SET name = @Name, updated_at = @UpdatedAt, provider_id = @ProviderId, 
              model_id = @ModelId, system_prompt_id = @SystemPromptId, is_active = @IsActive, active_mode = @ActiveMode 
              WHERE id = @Id",
            session);
        return session;
    }

    public async Task DeleteAsync(string id)
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync("DELETE FROM sessions WHERE id = @Id", new { Id = id });
    }

    public async Task<Session?> GetActiveAsync()
    {
        using var connection = await GetOpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<Session>(
            "SELECT * FROM sessions WHERE is_active = 1");
    }

    public async Task SetActiveAsync(string id)
    {
        using var connection = await GetOpenConnectionAsync();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync("UPDATE sessions SET is_active = 0", transaction: transaction);
        await connection.ExecuteAsync("UPDATE sessions SET is_active = 1 WHERE id = @Id", new { Id = id }, transaction: transaction);
        transaction.Commit();
    }
}
