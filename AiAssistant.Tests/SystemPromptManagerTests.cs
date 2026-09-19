using AiAssistant.Core.Services;
using AiAssistant.Storage.Models;
using AiAssistant.Storage.Repositories;
using Microsoft.Extensions.Logging;
using Moq;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiAssistant.Tests;

public class SystemPromptManagerTests
{
    private readonly Mock<ISystemPromptRepository> _repoMock;
    private readonly Mock<ILogger<SystemPromptManager>> _loggerMock;
    private readonly SystemPromptManager _manager;

    public SystemPromptManagerTests()
    {
        _repoMock = new Mock<ISystemPromptRepository>();
        _loggerMock = new Mock<ILogger<SystemPromptManager>>();
        _manager = new SystemPromptManager(_repoMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task CreatePromptAsync_CreatesAndReturnsPrompt()
    {
        _repoMock.Setup(r => r.AddAsync(It.IsAny<SystemPrompt>())).ReturnsAsync((SystemPrompt p) => p);

        var result = await _manager.CreatePromptAsync("Custom", "You are an AI.", true);

        Assert.Equal("Custom", result.Name);
        Assert.Equal("You are an AI.", result.Content);
        Assert.True(result.IsDefault);
        _repoMock.Verify(r => r.AddAsync(It.IsAny<SystemPrompt>()), Times.Once);
    }

    [Fact]
    public async Task GetDefaultPromptAsync_ReturnsDefaultPrompt()
    {
        var expected = new SystemPrompt { Id = "1", IsDefault = true };
        _repoMock.Setup(r => r.GetDefaultAsync()).ReturnsAsync(expected);

        var result = await _manager.GetDefaultPromptAsync();

        Assert.NotNull(result);
        Assert.True(result.IsDefault);
    }

    [Fact]
    public async Task GetInterpolatedPromptAsync_InterpolatesVariables()
    {
        var prompt = new SystemPrompt { Id = "1", Content = "Hello {{Name}}, welcome to {{Place}}!" };
        _repoMock.Setup(r => r.GetByIdAsync("1")).ReturnsAsync(prompt);

        var variables = new Dictionary<string, string>
        {
            { "Name", "Alice" },
            { "Place", "Wonderland" }
        };

        var result = await _manager.GetInterpolatedPromptAsync("1", variables);

        Assert.Equal("Hello Alice, welcome to Wonderland!", result);
    }

    [Fact]
    public async Task GetInterpolatedPromptAsync_ReturnsDefaultIfIdIsNull()
    {
        var prompt = new SystemPrompt { Id = "def", Content = "I am default." };
        _repoMock.Setup(r => r.GetDefaultAsync()).ReturnsAsync(prompt);

        var result = await _manager.GetInterpolatedPromptAsync(null);

        Assert.Equal("I am default.", result);
    }

    [Fact]
    public async Task SetDefaultPromptAsync_CallsRepository()
    {
        await _manager.SetDefaultPromptAsync("123");

        _repoMock.Verify(r => r.SetDefaultAsync("123"), Times.Once);
    }

    [Fact]
    public async Task UpdatePromptAsync_UpdatesAndReturns()
    {
        var prompt = new SystemPrompt { Id = "1", Name = "Updated" };
        _repoMock.Setup(r => r.UpdateAsync(prompt)).ReturnsAsync(prompt);

        var result = await _manager.UpdatePromptAsync(prompt);

        Assert.Equal("Updated", result.Name);
        _repoMock.Verify(r => r.UpdateAsync(prompt), Times.Once);
    }
}
