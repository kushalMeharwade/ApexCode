using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AiAssistant.Storage.Database;
using AiAssistant.Storage.Models;
using Dapper;

namespace AiAssistant.Storage.Repositories;

public class CheckpointRepository : ICheckpointRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public CheckpointRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task SaveAsync(Checkpoint checkpoint)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync();
        const string sql = @"
            INSERT INTO checkpoints (id, session_id, message_id, commit_hash, workspace_path, description, created_at)
            VALUES (@Id, @SessionId, @MessageId, @CommitHash, @WorkspacePath, @Description, @CreatedAt)
            ON CONFLICT(id) DO UPDATE SET
                session_id = @SessionId,
                message_id = @MessageId,
                commit_hash = @CommitHash,
                workspace_path = @WorkspacePath,
                description = @Description,
                created_at = @CreatedAt;";
        
        // Convert dates to string for SQLite
        var parameters = new
        {
            checkpoint.Id,
            checkpoint.SessionId,
            checkpoint.MessageId,
            checkpoint.CommitHash,
            checkpoint.WorkspacePath,
            checkpoint.Description,
            CreatedAt = checkpoint.CreatedAt.ToString("O")
        };

        await connection.ExecuteAsync(sql, parameters);
    }

    public async Task<Checkpoint?> GetByIdAsync(string id)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync();
        const string sql = "SELECT * FROM checkpoints WHERE id = @Id;";
        var result = await connection.QuerySingleOrDefaultAsync<dynamic>(sql, new { Id = id });
        return result != null ? Map(result) : null;
    }

    public async Task<Checkpoint?> GetByMessageIdAsync(string messageId)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync();
        const string sql = "SELECT * FROM checkpoints WHERE message_id = @MessageId;";
        var result = await connection.QuerySingleOrDefaultAsync<dynamic>(sql, new { MessageId = messageId });
        return result != null ? Map(result) : null;
    }

    public async Task<IEnumerable<Checkpoint>> GetBySessionIdAsync(string sessionId)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync();
        const string sql = "SELECT * FROM checkpoints WHERE session_id = @SessionId ORDER BY created_at ASC;";
        var results = await connection.QueryAsync<dynamic>(sql, new { SessionId = sessionId });
        
        var checkpoints = new List<Checkpoint>();
        foreach (var row in results)
        {
            checkpoints.Add(Map(row));
        }
        return checkpoints;
    }

    public async Task DeleteBySessionIdAsync(string sessionId)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync();
        const string sql = "DELETE FROM checkpoints WHERE session_id = @SessionId;";
        await connection.ExecuteAsync(sql, new { SessionId = sessionId });
    }

    private static Checkpoint Map(dynamic row)
    {
        return new Checkpoint
        {
            Id = row.id,
            SessionId = row.session_id,
            MessageId = row.message_id,
            CommitHash = row.commit_hash,
            WorkspacePath = row.workspace_path,
            Description = row.description,
            CreatedAt = DateTime.Parse(row.created_at).ToUniversalTime()
        };
    }
}
