using AiAssistant.Llm.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using AiAssistant.Core.Services;
using AiAssistant.Storage.Repositories;
using System;
using Xunit;

namespace AiAssistant.Tests;

public class ChatClientFactoryTests
{
    private readonly Mock<ILogger<ChatClientFactory>> _loggerMock;
    private readonly Mock<ILogBus> _logBusMock;
    private readonly Mock<IModelCacheRepository> _modelCacheRepoMock;

    public ChatClientFactoryTests()
    {
        _loggerMock = new Mock<ILogger<ChatClientFactory>>();
        _logBusMock = new Mock<ILogBus>();
        _modelCacheRepoMock = new Mock<IModelCacheRepository>();
    }

    [Fact]
    public void CreateClient_OpenAI_CreatesClientSuccessfully()
    {
        var factory = new ChatClientFactory(_loggerMock.Object, _logBusMock.Object, _modelCacheRepoMock.Object);

        var client = factory.CreateClient("openai", "gpt-4", "test-key");

        Assert.NotNull(client);
    }

    [Fact]
    public void CreateClient_UnknownProvider_ThrowsException()
    {
        var factory = new ChatClientFactory(_loggerMock.Object, _logBusMock.Object, _modelCacheRepoMock.Object);

        var ex = Assert.Throws<NotSupportedException>(() => 
            factory.CreateClient("unknown-provider", "model-id", "test-key"));

        Assert.Contains("is not registered", ex.Message);
    }

    [Fact]
    public void RegisterProvider_AllowsCreation()
    {
        var factory = new ChatClientFactory(_loggerMock.Object, _logBusMock.Object, _modelCacheRepoMock.Object);
        var mockClient = new Mock<IChatClient>();

        factory.RegisterProvider("custom", (model, key, endpoint) => mockClient.Object);

        var client = factory.CreateClient("custom", "model-id", "key");

        // The factory now wraps the client in a DiagnosticsLoggingChatClient for telemetry
        var wrapper = Assert.IsType<DiagnosticsLoggingChatClient>(client);
    }

    [Fact]
    public void GetDefaultClient_UsesResolverAndCreatesOpenAI()
    {
        bool resolverCalled = false;
        var factory = new ChatClientFactory(_loggerMock.Object, _logBusMock.Object, _modelCacheRepoMock.Object, provider =>
        {
            resolverCalled = true;
            return "default-key";
        });

        var client = factory.GetDefaultClient();

        Assert.NotNull(client);
        Assert.True(resolverCalled);
    }
}
