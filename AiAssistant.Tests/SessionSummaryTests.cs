using System;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using AiAssistant.Storage.Database;
using AiAssistant.Storage.Repositories;
using Dapper;
using Moq;
using Xunit;

namespace AiAssistant.Tests;

public class SessionSummaryTests
{
    [Fact]
    public async Task SummariesReturnOnlyFirstUserPromptAndIncludeEmptySessions()
    {
        var connectionString = "FullUri=file:history-" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared";
        using var keeper = new SQLiteConnection(connectionString);
        await keeper.OpenAsync();
        // Intentionally omit serialized content/metadata columns: the summary must
        // never depend on fetching or deserializing full message payloads.
        await keeper.ExecuteAsync(@"
            CREATE TABLE sessions (id TEXT PRIMARY KEY, name TEXT, updated_at TEXT);
            CREATE TABLE messages (session_id TEXT, role TEXT, content TEXT, original_prompt TEXT, timestamp TEXT);
            CREATE INDEX idx_messages_session_role_timestamp ON messages(session_id, role, timestamp);
            INSERT INTO sessions VALUES ('s', 'Renamed', '2026-01-03');
            INSERT INTO sessions VALUES ('empty', 'Empty', '2026-01-02');
            INSERT INTO sessions VALUES ('legacy', 'Legacy', '2026-01-01');
            INSERT INTO messages VALUES ('s','system','ignore',NULL,'2025-12-31');
            INSERT INTO messages VALUES ('s','user','later',NULL,'2026-01-02');
            INSERT INTO messages VALUES ('s','user','[CONTEXT: injected]','original question','2026-01-01');
            INSERT INTO messages VALUES ('s','user','same timestamp later row',NULL,'2026-01-01');
            INSERT INTO messages VALUES ('legacy','assistant','ignore',NULL,'2025-12-31');
            INSERT INTO messages VALUES ('legacy','user','legacy question','','2026-01-01');");
        var factory = new Mock<IDbConnectionFactory>();
        factory.Setup(f => f.CreateConnectionAsync()).ReturnsAsync(() => new SQLiteConnection(connectionString));
        var summaries = (await new SessionRepository(factory.Object).GetSummariesAsync()).ToList();

        Assert.Equal(new[] { "s", "empty", "legacy" }, summaries.Select(s => s.Id));
        Assert.Equal("Renamed", summaries[0].Name);
        Assert.Equal(new DateTime(2026, 1, 3), summaries[0].UpdatedAt);
        Assert.Equal("original question", summaries[0].FirstPrompt);
        Assert.Null(summaries[1].FirstPrompt);
        Assert.Equal("legacy question", summaries[2].FirstPrompt);
        factory.Verify(f => f.CreateConnectionAsync(), Times.Once);
    }
}
