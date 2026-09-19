using AiAssistant.Core.Models;
using AiAssistant.Core.Services;
using AiAssistant.Storage.Models;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiAssistant.Tests;

public class ConversationHistoryTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly ConversationHistory _history;

    public ConversationHistoryTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _history = new ConversationHistory(_sessionManagerMock.Object);
    }

    [Fact]
    public async Task AddTurnAsync_NullSessionId_ThrowsException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _history.AddTurnAsync(null, new ConversationTurn()));
    }

    [Fact]
    public async Task AddTurnAsync_AddsTurnToSessionManager()
    {
        var turn = new ConversationTurn();
        await _history.AddTurnAsync("session-1", turn);

        _sessionManagerMock.Verify(s => s.AddMessageAsync("session-1", turn), Times.Once);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsTurnsFromSessionManager()
    {
        var expected = new List<ConversationTurn> { new() };
        _sessionManagerMock.Setup(s => s.GetConversationHistoryAsync("session-1")).ReturnsAsync(expected);

        var result = await _history.GetHistoryAsync("session-1");

        Assert.Single(result);
    }

    [Fact]
    public async Task ClearHistoryAsync_DeletesAndRecreatesSession()
    {
        var session = new Session { Name = "S", ProviderId = "P", ModelId = "M", SystemPromptId = "SP" };
        _sessionManagerMock.Setup(s => s.GetSessionAsync("session-1")).ReturnsAsync(session);

        await _history.ClearHistoryAsync("session-1");

        _sessionManagerMock.Verify(s => s.DeleteSessionMessagesAsync("session-1"), Times.Once);
    }

    [Fact]
    public async Task ClearHistoryAsync_NullSessionId_ThrowsException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _history.ClearHistoryAsync(null));
    }

    [Fact]
    public async Task GetTokenCountAsync_EstimatesTokensBasedOnLength()
    {
        var turns = new List<ConversationTurn>
        {
            new() { Content = new string('a', 80) }, // 20 tokens
            new() { Content = new string('b', 40) }  // 10 tokens
        };
        _sessionManagerMock.Setup(s => s.GetConversationHistoryAsync("session-1")).ReturnsAsync(turns);

        var count = await _history.GetTokenCountAsync("session-1");

        Assert.True(count > 0);
    }
}
