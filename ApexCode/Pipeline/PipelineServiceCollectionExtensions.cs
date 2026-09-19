using AiAssistant.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace ApexCode.Pipeline;

public static class PipelineServiceCollectionExtensions
{
    public static IServiceCollection AddChatRequestPipeline(this IServiceCollection services)
    {
        services.AddSingleton<IContextCollector, SystemContextCollector>();
        services.AddSingleton<IContextCollector, WorkspaceContextCollector>();
        services.AddSingleton<IContextCollector, PersonaContextCollector>();
        services.AddSingleton<IContextCollector, ToolContextCollector>();

        services.AddSingleton<IPromptSection, PersonaSection>();
        services.AddSingleton<IPromptSection, EnvironmentSection>();
        services.AddSingleton<IPromptSection, FilePathsSection>();
        // ToolsSection intentionally removed — tool definitions live in ChatOptions.Tools JSON schema.
        services.AddSingleton<IPromptSection, RulesSection>();
        services.AddSingleton<SystemPromptBuilder>();
        services.AddSingleton<SystemPromptCache>();

        services.AddSingleton<IProviderAdapter, OpenAIAdapter>();
        services.AddSingleton<IProviderAdapter, AnthropicAdapter>();
        services.AddSingleton<IProviderAdapter, GeminiAdapter>();
        services.AddSingleton<ProviderAdapterResolver>();
        services.AddSingleton<IChatRequestPipeline, ChatRequestPipeline>();
        return services;
    }
}
