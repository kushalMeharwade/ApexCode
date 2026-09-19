using AiAssistant.Storage.Database;
using AiAssistant.Storage.Models;
using Dapper;
using System.Data.SQLite;
using System.Data;

namespace AiAssistant.Storage.Repositories;

public class MessageRepository : IMessageRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public MessageRepository(IDbConnectionFactory connectionFactory)
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

    public async Task<Message?> GetByIdAsync(string id)
    {
        using var connection = await GetOpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<Message>(
            "SELECT * FROM messages WHERE id = @Id", new { Id = id });
    }

    public async Task<IEnumerable<Message>> GetBySessionIdAsync(string sessionId)
    {
        using var connection = await GetOpenConnectionAsync();
        return await connection.QueryAsync<Message>(
            "SELECT * FROM messages WHERE session_id = @SessionId ORDER BY timestamp, rowid", new { SessionId = sessionId });
    }

    public async Task<Message> AddAsync(Message message)
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            @"INSERT INTO messages (id, session_id, role, content, timestamp, token_count, response_time, is_streaming, serialized_content_blocks, original_prompt, metadata) 
              VALUES (@Id, @SessionId, @Role, @Content, @Timestamp, @TokenCount, @ResponseTime, @IsStreaming, @SerializedContentBlocks, @OriginalPrompt, @Metadata)",
            message);
        return message;
    }

    public async Task<Message> UpdateAsync(Message message)
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            @"UPDATE messages SET content = @Content, timestamp = @Timestamp, token_count = @TokenCount, 
              response_time = @ResponseTime, is_streaming = @IsStreaming, serialized_content_blocks = @SerializedContentBlocks, original_prompt = @OriginalPrompt, metadata = @Metadata WHERE id = @Id",
            message);
        return message;
    }

    public async Task DeleteAsync(string id)
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync("DELETE FROM messages WHERE id = @Id", new { Id = id });
    }

    public async Task DeleteBySessionIdAsync(string sessionId)
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync("DELETE FROM messages WHERE session_id = @SessionId", new { SessionId = sessionId });
    }

    public async Task DeleteFromMessageAsync(string sessionId, string messageId)
    {
        using var connection = await GetOpenConnectionAsync();
        using var transaction = connection.BeginTransaction();
        var boundary = await connection.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT timestamp, rowid AS sequence FROM messages WHERE session_id = @sessionId AND id = @messageId",
            new { sessionId, messageId }, transaction);
        if (boundary == null)
            throw new InvalidOperationException("The checkpoint's message is no longer in this conversation.");

        var parameters = new { sessionId, timestamp = (object)boundary.timestamp, sequence = (long)boundary.sequence };
        const string removedMessages = @"SELECT id FROM messages WHERE session_id = @sessionId
            AND (timestamp > @timestamp OR (timestamp = @timestamp AND rowid >= @sequence))";
        await connection.ExecuteAsync("DELETE FROM checkpoints WHERE session_id = @sessionId AND message_id IN (" + removedMessages + ")", parameters, transaction);
        await connection.ExecuteAsync("DELETE FROM messages WHERE id IN (" + removedMessages + ")", parameters, transaction);
        await connection.ExecuteAsync("UPDATE sessions SET updated_at = @updatedAt WHERE id = @sessionId",
            new { sessionId, updatedAt = DateTime.UtcNow }, transaction);
        transaction.Commit();
    }
}
