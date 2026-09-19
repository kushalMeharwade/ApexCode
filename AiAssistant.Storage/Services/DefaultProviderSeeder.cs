using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Storage.Models;
using AiAssistant.Storage.Repositories;

namespace AiAssistant.Storage.Services;

public interface IDefaultProviderSeeder
{
    Task SeedAsync(CancellationToken ct = default);
}

public class DefaultProviderSeeder : IDefaultProviderSeeder
{
    private readonly IProviderProfileRepository _providerRepository;

    private static readonly IReadOnlyList<ProviderProfile> DefaultProviders = new List<ProviderProfile>
    {
        new ProviderProfile {
            BuiltInId = "openai", Name = "OpenAI", ProviderType = "openai",
            ApiEndpoint = "https://api.openai.com/v1",
            ModelFetchEndpoint = "https://api.openai.com/v1/models",
            DefaultModel = "gpt-4o", LogoResourceKey = "openai", IsBuiltIn = true, ApiKey = null, ContextWindowTokens = 128000
        },
        new ProviderProfile {
            BuiltInId = "openrouter", Name = "OpenRouter", ProviderType = "openrouter",
            ApiEndpoint = "https://openrouter.ai/api/v1",
            ModelFetchEndpoint = "https://openrouter.ai/api/v1/models",
            DefaultModel = "openai/gpt-4o", LogoResourceKey = "openrouter", IsBuiltIn = true, ApiKey = null, ContextWindowTokens = 128000
        },
        new ProviderProfile {
            BuiltInId = "deepseek", Name = "DeepSeek", ProviderType = "openai", // OpenAI compatible
            ApiEndpoint = "https://api.deepseek.com",
            ModelFetchEndpoint = "https://api.deepseek.com/models",
            DefaultModel = "deepseek-chat", LogoResourceKey = "deepseek", IsBuiltIn = true, ApiKey = null, ContextWindowTokens = 64000
        },
        new ProviderProfile {
            BuiltInId = "google", Name = "Google (Gemini)", ProviderType = "openai", // OpenAI compatible
            ApiEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai/",
            ModelFetchEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai/models",
            DefaultModel = "gemini-2.5-flash", LogoResourceKey = "gemini", IsBuiltIn = true, ApiKey = null, ContextWindowTokens = 1000000
        },
        new ProviderProfile {
            BuiltInId = "nvidia-nim", Name = "NVIDIA NIM", ProviderType = "openai", // OpenAI compatible
            ApiEndpoint = "https://integrate.api.nvidia.com/v1",
            ModelFetchEndpoint = "https://integrate.api.nvidia.com/v1/models",
            DefaultModel = "meta/llama-3.3-70b-instruct", LogoResourceKey = "nvidia", IsBuiltIn = true, ApiKey = null, ContextWindowTokens = 128000
        },
        new ProviderProfile {
            BuiltInId = "vercel-ai-gateway", Name = "Vercel AI Gateway", ProviderType = "openai", // OpenAI compatible
            ApiEndpoint = "https://ai-gateway.vercel.sh/v1",
            ModelFetchEndpoint = "https://ai-gateway.vercel.sh/v1/models",
            DefaultModel = "openai/gpt-4o", LogoResourceKey = "vercel", IsBuiltIn = true, ApiKey = null, ContextWindowTokens = 128000
        }
    };

    public DefaultProviderSeeder(IProviderProfileRepository providerRepository)
    {
        _providerRepository = providerRepository;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var existingIds = (await _providerRepository.GetAllAsync())
            .Where(p => p.IsBuiltIn && p.BuiltInId != null)
            .Select(p => p.BuiltInId)
            .ToHashSet();

        foreach (var defaultProvider in DefaultProviders)
        {
            if (existingIds.Contains(defaultProvider.BuiltInId))
                continue; // already seeded — never touch ApiKey/ApiEndpoint/ModelFetchEndpoint/DefaultModel on an existing row

            await _providerRepository.AddAsync(defaultProvider);
        }
    }
}
