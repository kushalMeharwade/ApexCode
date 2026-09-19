using System;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using AiAssistant.Core.Models;
using AiAssistant.Core.Services;
using AiAssistant.Engine.Services;
using AiAssistant.Storage.Database;
using AiAssistant.Storage.Models;
using AiAssistant.Storage.Repositories;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AiAssistant.Tests;

public class CheckpointRestoreTests
{
    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    public async Task InvalidRestoreTypeDoesNotChangeAnything(string restoreType)
    {
        var checkpoints = new Mock<ICheckpointRepository>(MockBehavior.Strict);
        var sessions = new Mock<ISessionManager>(MockBehavior.Strict);
        var service = new CheckpointService(checkpoints.Object, sessions.Object, NullLogger<CheckpointService>.Instance);
        Assert.False((await service.RestoreCheckpointAsync("checkpoint", restoreType)).Success);
    }

    [Fact]
    public async Task AfterTurnCheckpointCannotRewindChatToBeforeRequest()
    {
        var checkpoints = new Mock<ICheckpointRepository>();
        var sessions = new Mock<ISessionManager>();
        checkpoints.Setup(c => c.GetByIdAsync("checkpoint")).ReturnsAsync(new Checkpoint
        { SessionId = "session", MessageId = "request", Description = "After Tool Execution" });
        sessions.Setup(s => s.GetConversationHistoryAsync("session")).ReturnsAsync(new[]
        { new ConversationTurn { Id = "request", Role = "user" } });
        var service = new CheckpointService(checkpoints.Object, sessions.Object, NullLogger<CheckpointService>.Instance);
        Assert.False((await service.RestoreCheckpointAsync("checkpoint", "task")).Success);
        sessions.Verify(s => s.RevertMessagesAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SessionManagerPreservesTurnId()
    {
        var messages = new Mock<IMessageRepository>();
        var sessions = new Mock<ISessionRepository>();
        var manager = new SessionManager(sessions.Object, messages.Object, NullLogger<SessionManager>.Instance);
        var turn = new ConversationTurn { Id = "request", Role = "user", Content = "create test2.txt" };
        await manager.AddMessageAsync("session", turn);
        messages.Verify(m => m.AddAsync(It.Is<Message>(v => v.Id == turn.Id && v.Content == turn.Content)), Times.Once);
    }

    [Fact]
    public async Task ChatOnlyRestoreDoesNotRequireGitAndDeletesSelectedRequest()
    {
        var checkpoints = new Mock<ICheckpointRepository>();
        var sessions = new Mock<ISessionManager>();
        checkpoints.Setup(c => c.GetByIdAsync("checkpoint")).ReturnsAsync(new Checkpoint
        { Id = "checkpoint", SessionId = "session", MessageId = "request", Description = "Before AI Turn" });
        sessions.Setup(s => s.GetConversationHistoryAsync("session")).ReturnsAsync(new[]
        { new ConversationTurn { Id = "request", Role = "user" } });
        var service = new CheckpointService(checkpoints.Object, sessions.Object, NullLogger<CheckpointService>.Instance);
        var result = await service.RestoreCheckpointAsync("checkpoint", "task");
        Assert.True(result.Success, result.ErrorMessage);
        sessions.Verify(s => s.RevertMessagesAsync("session", "request"), Times.Once);
    }

    [Fact]
    public async Task LegacyMismatchedMessageDoesNotDeleteHistory()
    {
        var checkpoints = new Mock<ICheckpointRepository>();
        var sessions = new Mock<ISessionManager>();
        checkpoints.Setup(c => c.GetByIdAsync("checkpoint")).ReturnsAsync(new Checkpoint
        { SessionId = "session", MessageId = "old-unrelated-id", Description = "Before AI Turn" });
        sessions.Setup(s => s.GetConversationHistoryAsync("session")).ReturnsAsync(new[]
        { new ConversationTurn { Id = "actual-id", Role = "user" } });
        var service = new CheckpointService(checkpoints.Object, sessions.Object, NullLogger<CheckpointService>.Instance);
        var result = await service.RestoreCheckpointAsync("checkpoint", "task");
        Assert.False(result.Success);
        sessions.Verify(s => s.RevertMessagesAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RepositoryRewindPersistsAndPreservesEarlierAndOtherSessionMessages()
    {
        // Shared in-memory SQLite stays alive through keeper; each repository call opens
        // a separate connection, just like reopening history after a restore.
        var connectionString = "FullUri=file:checkpoint-" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared";
        using var keeper = new SQLiteConnection(connectionString);
        await keeper.OpenAsync();
        await keeper.ExecuteAsync(@"
            CREATE TABLE sessions (id TEXT PRIMARY KEY, updated_at TEXT);
            CREATE TABLE messages (id TEXT PRIMARY KEY, session_id TEXT, role TEXT, content TEXT, timestamp TEXT);
            CREATE TABLE checkpoints (id TEXT PRIMARY KEY, session_id TEXT, message_id TEXT);
            INSERT INTO sessions VALUES ('s', NULL);
            INSERT INTO messages VALUES ('u1','s','user','create test1','2026-01-01');
            INSERT INTO messages VALUES ('a1','s','assistant','created test1','2026-01-01');
            INSERT INTO messages VALUES ('u2','s','user','create test2','2026-01-01');
            INSERT INTO messages VALUES ('a2','s','assistant','created test2','2026-01-01');
            INSERT INTO messages VALUES ('other','other','user','keep','2026-01-02');
            INSERT INTO checkpoints VALUES ('c1','s','u1');
            INSERT INTO checkpoints VALUES ('c2','s','u2');
            INSERT INTO checkpoints VALUES ('c3','other','other');");
        var factory = new Mock<IDbConnectionFactory>();
        factory.Setup(f => f.CreateConnectionAsync()).ReturnsAsync(() => new SQLiteConnection(connectionString));
        var repository = new MessageRepository(factory.Object);

        await repository.DeleteFromMessageAsync("s", "u2");

        Assert.Equal(new[] { "u1", "a1" }, (await repository.GetBySessionIdAsync("s")).Select(m => m.Id));
        Assert.Single(await repository.GetBySessionIdAsync("other"));
        Assert.Equal(new[] { "c1", "c3" }, await keeper.QueryAsync<string>("SELECT id FROM checkpoints ORDER BY id"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteFromMessageAsync("s", "missing"));
        Assert.Equal(2, (await repository.GetBySessionIdAsync("s")).Count());
    }
}
