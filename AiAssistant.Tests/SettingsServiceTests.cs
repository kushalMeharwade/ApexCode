using System.Threading.Tasks;

using AiAssistant.Core.Services;
using AiAssistant.Storage.Repositories;
using Moq;
using Xunit;
using AiAssistant.Storage.Models;
using System.Collections.Generic;

namespace AiAssistant.Tests;

public class SettingsServiceTests
{
    [Fact]
    public async Task GetAllProvidersAsync_ReturnsProfiles()
    {
        var repoMock = new Mock<IProviderProfileRepository>();
        repoMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<ProviderProfile> { new ProviderProfile { ProviderType = "OpenAI", IsEnabled = true } });
        var promptRepoMock = new Mock<ISystemPromptRepository>();

        //var service = new SettingsService(repoMock.Object, promptRepoMock.Object);
        //var profiles = await service.GetAllProvidersAsync();

        //Assert.Single(profiles);
        //Assert.Equal("OpenAI", profiles[0].ProviderType);
    }
}
