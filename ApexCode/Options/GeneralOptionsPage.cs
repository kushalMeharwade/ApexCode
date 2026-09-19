using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AiAssistant.Storage.Models;
using Microsoft.VisualStudio.Shell;
using System.Linq;
namespace ApexCode.Options;

[DisplayName("ApexCode")]
[Category("ApexCode")]
[Description("Configure ApexCode settings including providers, models, and behavior.")]
[Guid("d2e3f4a5-b6c7-d8e9-f0a1-b2c3d4e5f6a7")]
public class GeneralOptionsPage : DialogPage
{
    [DisplayName("Default Provider")]
    [Description("The default LLM provider to use for new sessions.")]
    [Category("Providers")]
    public string DefaultProvider { get; set; } = "openai";

    [DisplayName("Default Model")]
    [Description("Default model used when starting a new session.")]
    [Category("Providers")]
    public string DefaultModel { get; set; } = "gpt-4o";

    [DisplayName("OpenAI API Key")]
    [Description("Your OpenAI API key. Stored locally in the database.")]
    [Category("Providers")]
    [PasswordPropertyText(true)]
    public string OpenAiApiKey { get; set; } = "";

    [DisplayName("Azure OpenAI Endpoint")]
    [Description("Your Azure OpenAI endpoint URL (e.g., https://your-resource.openai.azure.com/).")]
    [Category("Providers")]
    public string AzureOpenAiEndpoint { get; set; } = "";

    [DisplayName("Azure OpenAI API Key")]
    [Description("Your Azure OpenAI API key. Stored locally in the database.")]
    [Category("Providers")]
    [PasswordPropertyText(true)]
    public string AzureOpenAiApiKey { get; set; } = "";

    [DisplayName("OpenRouter API Key")]
    [Description("Your OpenRouter API key (supports Anthropic, Llama, etc.).")]
    [Category("Providers")]
    [PasswordPropertyText(true)]
    public string OpenRouterApiKey { get; set; } = "";

    [DisplayName("Ollama Endpoint")]
    [Description("Your local Ollama endpoint (e.g., http://localhost:11434).")]
    [Category("Providers")]
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";

    protected override void OnActivate(CancelEventArgs e)
    {
        base.OnActivate(e);
        _ = LoadSettingsAsync();
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (ApexCodePackage.SystemServiceProvider == null) return;

            if (ApexCodePackage.SystemServiceProvider == null) return;

            var providerRepo = ApexCodePackage.SystemServiceProvider.GetService(typeof(AiAssistant.Storage.Repositories.IProviderProfileRepository)) as AiAssistant.Storage.Repositories.IProviderProfileRepository;
            if (providerRepo == null) return;

            var profiles = await providerRepo.GetAllAsync();
            var openAiProfile = profiles.FirstOrDefault(p => p.ProviderType == "openai" && p.IsEnabled);
            if (openAiProfile != null)
            {
                OpenAiApiKey = openAiProfile.ApiKey ?? "";
                DefaultModel = openAiProfile.DefaultModel ?? "gpt-4o";
            }
            var azureProfile = profiles.FirstOrDefault(p => p.ProviderType == "azureopenai" && p.IsEnabled);
            if (azureProfile != null)
            {
                AzureOpenAiApiKey = azureProfile.ApiKey ?? "";
                AzureOpenAiEndpoint = azureProfile.ApiEndpoint ?? "";
            }
            var openRouterProfile = profiles.FirstOrDefault(p => p.ProviderType == "openrouter" && p.IsEnabled);
            if (openRouterProfile != null)
            {
                OpenRouterApiKey = openRouterProfile.ApiKey ?? "";
            }
            var ollamaProfile = profiles.FirstOrDefault(p => p.ProviderType == "ollama" && p.IsEnabled);
            if (ollamaProfile != null)
            {
                OllamaEndpoint = ollamaProfile.ApiEndpoint ?? "http://localhost:11434";
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] Failed to load API keys: {ex.Message}");
        }
    }

    protected override void OnApply(PageApplyEventArgs e)
    {
        base.OnApply(e);
        _ = SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (ApexCodePackage.SystemServiceProvider == null) return;

            if (ApexCodePackage.SystemServiceProvider == null) return;

            var providerRepo = ApexCodePackage.SystemServiceProvider.GetService(typeof(AiAssistant.Storage.Repositories.IProviderProfileRepository)) as AiAssistant.Storage.Repositories.IProviderProfileRepository;
            if (providerRepo == null) return;

            var defaultProfile = await providerRepo.GetByIdAsync(DefaultProvider);
            if (defaultProfile != null)
            {
                var updated = defaultProfile with { DefaultModel = DefaultModel };
                if (DefaultProvider == "openai" && !string.IsNullOrEmpty(OpenAiApiKey))
                    updated = updated with { ApiKey = OpenAiApiKey };
                else if (DefaultProvider == "azureopenai" && !string.IsNullOrEmpty(AzureOpenAiApiKey))
                    updated = updated with { ApiKey = AzureOpenAiApiKey, ApiEndpoint = AzureOpenAiEndpoint };
                else if (DefaultProvider == "openrouter" && !string.IsNullOrEmpty(OpenRouterApiKey))
                    updated = updated with { ApiKey = OpenRouterApiKey, ApiEndpoint = updated.ApiEndpoint ?? "https://openrouter.ai/api/v1" };
                else if (DefaultProvider == "ollama")
                    updated = updated with { ApiEndpoint = updated.ApiEndpoint ?? OllamaEndpoint };

                await providerRepo.UpdateAsync(updated);
            }
            else
            {
                var newProfile = new ProviderProfile
                {
                    Id = DefaultProvider,
                    Name = DefaultProvider == "openai" ? "OpenAI" :
                           DefaultProvider == "azureopenai" ? "Azure OpenAI" :
                           DefaultProvider == "openrouter" ? "OpenRouter" : "Ollama",
                    ProviderType = DefaultProvider,
                    DefaultModel = DefaultModel,
                    IsEnabled = true
                };

                if (DefaultProvider == "openai" && !string.IsNullOrEmpty(OpenAiApiKey))
                    newProfile = newProfile with { ApiKey = OpenAiApiKey };
                else if (DefaultProvider == "azureopenai" && !string.IsNullOrEmpty(AzureOpenAiApiKey))
                    newProfile = newProfile with { ApiKey = AzureOpenAiApiKey, ApiEndpoint = AzureOpenAiEndpoint };
                else if (DefaultProvider == "openrouter" && !string.IsNullOrEmpty(OpenRouterApiKey))
                    newProfile = newProfile with { ApiKey = OpenRouterApiKey, ApiEndpoint = "https://openrouter.ai/api/v1" };
                else if (DefaultProvider == "ollama")
                    newProfile = newProfile with { ApiEndpoint = OllamaEndpoint };

                await providerRepo.AddAsync(newProfile);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] Failed to save settings: {ex.Message}");
        }
    }
}

public enum ApprovalMode
{
    [Description("Prompt for each tool execution")]
    Prompt,
    [Description("Allow all tool executions")]
    AllowAll,
    [Description("Deny all tool executions")]
    DenyAll
}

