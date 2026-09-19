using AiAssistant.Core.Models;
using AiAssistant.Core.Services;
using AiAssistant.Storage.Models;
using AiAssistant.Storage.Repositories;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiAssistant.Tests;

public class SessionManagerTests
{
    private readonly Mock<ISessionRepository> _sessionRepoMock;
    private readonly Mock<IMessageRepository> _messageRepoMock;
    private readonly Mock<ILogger<SessionManager>> _loggerMock;
    private readonly SessionManager _sessionManager;

    public SessionManagerTests()
    {
        _sessionRepoMock = new Mock<ISessionRepository>();
        _messageRepoMock = new Mock<IMessageRepository>();
        _loggerMock = new Mock<ILogger<SessionManager>>();
        
        _sessionManager = new SessionManager(
            _sessionRepoMock.Object, _messageRepoMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task CreateSessionAsync_CreatesSession()
    {
        // Arrange
        _sessionRepoMock.Setup(r => r.AddAsync(It.IsAny<Session>()))
                   .ReturnsAsync((Session s) => s);

        // Act
        var result = await _sessionManager.CreateSessionAsync("Test", "openai", "gpt-4", "default");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Test", result.Name);
        _sessionRepoMock.Verify(r => r.AddAsync(It.IsAny<Session>()), Times.Once);
    }

    [Fact]
    public async Task GetSessionAsync_ReturnsExpectedSession()
    {
        var expected = new Session { Id = "test-id", Name = "TestSession" };
        _sessionRepoMock.Setup(r => r.GetByIdAsync("test-id")).ReturnsAsync(expected);

        var result = await _sessionManager.GetSessionAsync("test-id");

        Assert.NotNull(result);
        Assert.Equal("TestSession", result.Name);
    }

    [Fact]
    public async Task GetAllSessionsAsync_ReturnsAllSessions()
    {
        var expected = new List<Session> { new() { Id = "1" }, new() { Id = "2" } };
        _sessionRepoMock.Setup(r => r.GetAllAsync()).ReturnsAsync(expected);

        var result = await _sessionManager.GetAllSessionsAsync();

        Assert.Equal(2, result.Count());
    }

    [Fact]
    public async Task UpdateSessionAsync_UpdatesAndReturnsSession()
    {
        var session = new Session { Id = "test-id", Name = "Updated" };
        _sessionRepoMock.Setup(r => r.UpdateAsync(session)).ReturnsAsync(session);

        var result = await _sessionManager.UpdateSessionAsync(session);

        Assert.Equal("Updated", result.Name);
        _sessionRepoMock.Verify(r => r.UpdateAsync(session), Times.Once);
    }

    [Fact]
    public async Task DeleteSessionAsync_DeletesSession()
    {
        await _sessionManager.DeleteSessionAsync("test-id");

        _sessionRepoMock.Verify(r => r.DeleteAsync("test-id"), Times.Once);
    }

    [Fact]
    public async Task SetActiveSessionAsync_UpdatesActiveStatus()
    {
        await _sessionManager.SetActiveSessionAsync("test-id");

        _sessionRepoMock.Verify(r => r.SetActiveAsync("test-id"), Times.Once);
    }

    [Fact]
    public async Task GetActiveSessionAsync_ReturnsActiveSession()
    {
        var expectedSession = new Session { Id = "1", Name = "Active", IsActive = true };
        _sessionRepoMock.Setup(r => r.GetActiveAsync()).ReturnsAsync(expectedSession);

        var result = await _sessionManager.GetActiveSessionAsync();

        Assert.NotNull(result);
        Assert.True(result.IsActive);
    }

    [Fact]
    public async Task AddMessageAsync_AddsMessageToSession()
    {
        var turn = new ConversationTurn { Content = "Hello", Role = "user" };

        await _sessionManager.AddMessageAsync("session-id", turn);

        _messageRepoMock.Verify(r => r.AddAsync(It.Is<Message>(m => m.SessionId == "session-id" && m.Content == "Hello")), Times.Once);
    }

    [Fact]
    public async Task GetConversationHistoryAsync_ReturnsMessagesAsTurns()
    {
        var messages = new List<Message>
        {
            new() { Role = "user", Content = "Hi", Timestamp = DateTime.UtcNow },
            new() { Role = "assistant", Content = "Hello", Timestamp = DateTime.UtcNow }
        };
        _messageRepoMock.Setup(r => r.GetBySessionIdAsync("session-id")).ReturnsAsync(messages);

        var history = await _sessionManager.GetConversationHistoryAsync("session-id");

        var turns = history.ToList();
        Assert.Equal(2, turns.Count);
        Assert.Equal("Hi", turns[0].Content);
        Assert.Equal("assistant", turns[1].Role);
    }
}
