using Dapper;
using System.Data.SQLite;
using System;
using System.IO;
using System.Threading.Tasks;

public record Message
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string SessionId { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

class Program
{
    static async Task Main()
    {
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
        var dbPath = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\AiAssistant\aiassistant.db");
        using var conn = new SQLiteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT session_id, count(*) FROM messages GROUP BY session_id ORDER BY count(*) DESC LIMIT 1;";
        var sessionId = (string)await cmd.ExecuteScalarAsync();
        
        Console.WriteLine($"Session: {sessionId}");
        
        var messages = await conn.QueryAsync<Message>("SELECT * FROM messages WHERE session_id = @SessionId", new { SessionId = sessionId });
        Console.WriteLine("WITHOUT ORDER BY:");
        foreach(var msg in messages)
        {
            Console.WriteLine($"{msg.Role} | {msg.Timestamp:O}");
        }
        
        Console.WriteLine("\nWITH ORDER BY timestamp:");
        var messagesOrdered = await conn.QueryAsync<Message>("SELECT * FROM messages WHERE session_id = @SessionId ORDER BY timestamp", new { SessionId = sessionId });
        foreach(var msg in messagesOrdered)
        {
            Console.WriteLine($"{msg.Role} | {msg.Timestamp:O}");
        }
    }
}
