using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using Azure.AI.OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using AiAssistant.Core.Services;
using AiAssistant.Storage.Repositories;
namespace AiAssistant.Llm.Services;

public class ChatClientFactory : IChatClientFactory
{
    private readonly Dictionary<string, Func<string, string, string?, IChatClient>> _providers = new();
    private readonly ILogger<ChatClientFactory> _logger;
    private string _defaultProvider = "openai";
    private string _defaultModel = "gpt-4";
    private ResiliencePipeline? _pipeline;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IChatClient> _clientCache = new();
    private readonly ILogBus _logBus;
    private readonly IModelCacheRepository _modelCacheRepo;

    public event EventHandler<LlmRetryInitiated>? OnRetry;

    public ChatClientFactory(ILogger<ChatClientFactory> logger, ILogBus logBus, IModelCacheRepository modelCacheRepo, Func<string, string?>? apiKeyResolver = null)
    {
        _logger = logger;
        _logBus = logBus;
        _modelCacheRepo = modelCacheRepo;
        _apiKeyResolver = apiKeyResolver;
        RegisterBuiltInProviders();
    }

    private readonly Func<string, string?>? _apiKeyResolver;

    public IChatClient CreateClient(string providerId, string modelId, string? apiKey = null, string? apiEndpoint = null)
    {
        if (_providers.TryGetValue(providerId, out var factory))
        {
            // If no API key was passed, try to load it from the resolver
            if (string.IsNullOrEmpty(apiKey) && _apiKeyResolver != null)
            {
                apiKey = _apiKeyResolver(providerId);
            }
            
            var key = apiKey ?? "";
            var endpoint = apiEndpoint ?? "";
            var cacheKey = $"{providerId}|{modelId}|{key}|{endpoint}";
            
            return _clientCache.GetOrAdd(cacheKey, _ => 
            {
                var rawClient = factory(modelId, key, endpoint);
                return new DiagnosticsLoggingChatClient(rawClient, _logBus, _modelCacheRepo, providerId, modelId);
            });
        }
        
        throw new NotSupportedException($"Provider '{providerId}' is not registered");
    }

    public void InvalidateCache()
    {
        foreach (var client in _clientCache.Values)
        {
            if (client is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        _clientCache.Clear();
        _logger.LogInformation("ChatClientFactory cache invalidated. Existing clients disposed.");
    }

    public IChatClient CreateClientWithTools(string providerId, string modelId, string? apiKey = null, string? apiEndpoint = null, IList<AITool>? tools = null)
    {
        var innerClient = CreateClient(providerId, modelId, apiKey, apiEndpoint);
        
        var resilientClient = new ResilientChatClient(innerClient, GetOrCreateResiliencePipeline());
        
        if (tools != null && tools.Count > 0)
        {
            var fixerClient = new DegenerateToolCallFixerClient(resilientClient);

            // Wrap the client with the function-invocation middleware so tool calls are executed
            // automatically and their results are fed back to the model.
            return new ChatClientBuilder(fixerClient)
                .UseFunctionInvocation(configure: c =>
                {
                    c.MaximumIterationsPerRequest = 10;
                })
                .Build();
        }
        return resilientClient;
    }

    private class DegenerateToolCallFixerClient : DelegatingChatClient
    {
        public DegenerateToolCallFixerClient(IChatClient innerClient) : base(innerClient)
        {
        }

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var response = await base.GetResponseAsync(chatMessages, options, cancellationToken);
            FixDegenerateToolCalls(response.Messages);
            return response;
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> chatMessages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var update in base.GetStreamingResponseAsync(chatMessages, options, cancellationToken))
            {
                if (update.Contents != null)
                {
                    for (int i = 0; i < update.Contents.Count; i++)
                    {
                        if (update.Contents[i] is FunctionCallContent fcc && string.IsNullOrWhiteSpace(fcc.Name))
                        {
                            var fixedArgs = fcc.Arguments ?? new Dictionary<string, object?>();
                            update.Contents[i] = new FunctionCallContent(fcc.CallId, "invalid_tool_call_handler", fixedArgs);
                        }
                    }
                }
                yield return update;
            }
        }
        
        private void FixDegenerateToolCalls(IEnumerable<Microsoft.Extensions.AI.ChatMessage>? messages)
        {
            if (messages == null) return;
            foreach (var msg in messages)
            {
                if (msg.Contents != null)
                {
                    for (int i = 0; i < msg.Contents.Count; i++)
                    {
                        if (msg.Contents[i] is FunctionCallContent fcc && string.IsNullOrWhiteSpace(fcc.Name))
                        {
                            var fixedArgs = fcc.Arguments ?? new Dictionary<string, object?>();
                            msg.Contents[i] = new FunctionCallContent(fcc.CallId, "invalid_tool_call_handler", fixedArgs);
                        }
                    }
                }
            }
        }
    }

    private ResiliencePipeline GetOrCreateResiliencePipeline()
    {
        if (_pipeline != null) return _pipeline;

        var builder = new ResiliencePipelineBuilder();

        // 1. Total Timeout (300s)
        builder.AddTimeout(TimeSpan.FromSeconds(300));

        // 2. Circuit Breaker (50% failure, 10s window, 30s cooldown)
        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(60),
            MinimumThroughput = 2,
            BreakDuration = TimeSpan.FromSeconds(30),
            ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => IsTransientError(ex))
        });

        // 3. Rate Limit (429) Retry
        builder.AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => IsRateLimitError(ex)),
            MaxRetryAttempts = 4,
            DelayGenerator = static args =>
            {
                var delay = GetRetryAfterDelay(args.Outcome.Exception);
                if (delay.HasValue) return new ValueTask<TimeSpan?>(delay.Value);
                // Fallback exponential with jitter
                var exp = TimeSpan.FromSeconds(Math.Pow(2, args.AttemptNumber));
                var jitter = TimeSpan.FromMilliseconds(new Random().Next(0, 1000));
                return new ValueTask<TimeSpan?>(exp + jitter);
            },
            OnRetry = args =>
            {
                OnRetry?.Invoke(this, new LlmRetryInitiated(args.RetryDelay, "Rate limit (429) reached"));
                return default;
            }
        });

        // 4. Bad Request (400) Retry
        builder.AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => IsBadRequestError(ex)),
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromSeconds(2),
            UseJitter = true,
            OnRetry = args =>
            {
                OnRetry?.Invoke(this, new LlmRetryInitiated(args.RetryDelay, "Bad request (400) encountered"));
                return default;
            }
        });

        // 5. Server Error (5xx) or Network Error Retry
        builder.AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => IsServerError(ex)),
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromSeconds(2),
            UseJitter = true,
            OnRetry = args =>
            {
                var reason = "Server error encountered";
                if (args.Outcome.Exception is HttpRequestException || args.Outcome.Exception?.InnerException is System.Net.Sockets.SocketException)
                {
                    reason = "Network disconnected or unreachable";
                }
                OnRetry?.Invoke(this, new LlmRetryInitiated(args.RetryDelay, reason));
                return default;
            }
        });

        // 6. Per-Try Timeout (180s)
        builder.AddTimeout(TimeSpan.FromSeconds(180));

        _pipeline = builder.Build();
        return _pipeline;
    }

    private static bool IsTransientError(Exception ex) => IsRateLimitError(ex) || IsServerError(ex) || IsBadRequestError(ex);

    private static bool IsBadRequestError(Exception? ex)
    {
        while (ex != null)
        {
            if (ex is HttpRequestException httpEx && httpEx.Message.Contains("400")) return true;
            if (ex is System.ClientModel.ClientResultException cre && cre.Status == 400) return true;
            if (ex.Message.Contains("400") || ex.Message.Contains("Bad Request")) return true;
            ex = ex.InnerException;
        }
        return false;
    }

    private static bool IsRateLimitError(Exception? ex)
    {
        while (ex != null)
        {
            if (ex is HttpRequestException httpEx && httpEx.Message.Contains("429")) return true;
            if (ex is System.ClientModel.ClientResultException cre && cre.Status == 429) return true;
            if (ex.Message.Contains("429") || ex.Message.Contains("Too Many Requests")) return true;
            ex = ex.InnerException;
        }
        return false;
    }

    private static bool IsServerError(Exception? ex)
    {
        while (ex != null)
        {
            if (ex is HttpRequestException httpEx)
            {
                if (httpEx.Message.Contains("500") || httpEx.Message.Contains("502") || httpEx.Message.Contains("503") || httpEx.Message.Contains("504")) return true;
                
                // Heuristic for network errors since StatusCode is not available in .NET Framework / Standard 2.0
                if (httpEx.Message.Contains("host") || httpEx.Message.Contains("connection") || httpEx.Message.Contains("network")) return true;
            }
            if (ex is System.Net.Sockets.SocketException) return true;
            if (ex is System.IO.IOException) return true;
            if (ex is System.ClientModel.ClientResultException cre)
            {
                if (cre.Status >= 500 && cre.Status <= 599) return true;
            }
            if (ex is TimeoutException) return true;
            if (ex is Polly.Timeout.TimeoutRejectedException) return true;
            if (ex is TaskCanceledException tce && !tce.CancellationToken.IsCancellationRequested) return true;
            
            ex = ex.InnerException;
        }
        return false;
    }

    private static TimeSpan? GetRetryAfterDelay(Exception? ex)
    {
        if (ex == null) return null;
        if (ex is System.ClientModel.ClientResultException cre)
        {
            if (cre.GetRawResponse()?.Headers.TryGetValue("Retry-After", out var retryAfter) == true)
            {
                if (int.TryParse(retryAfter, out var seconds)) return TimeSpan.FromSeconds(seconds);
            }
        }
        var msg = ex.Message;
        var match = System.Text.RegularExpressions.Regex.Match(msg, @"retry.*after.* (\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var s)) return TimeSpan.FromSeconds(s);

        return null;
    }

    public IChatClient GetDefaultClient()
    {
        var apiKey = _apiKeyResolver?.Invoke(_defaultProvider);
        return CreateClient(_defaultProvider, _defaultModel, apiKey, null);
    }

    private string? LoadApiKeyFromProfile(string providerId)
    {
        // Legacy method - resolver is now used instead
        return _apiKeyResolver?.Invoke(providerId);
    }

    public void RegisterProvider(string providerId, Func<string, string, string?, IChatClient> factory)
    {
        _providers[providerId] = factory;
        _logger.LogInformation("Registered provider: {ProviderId}", providerId);
    }

    public void UnregisterProvider(string providerId)
    {
        _providers.Remove(providerId);
        _logger.LogInformation("Unregistered provider: {ProviderId}", providerId);
    }

    private void RegisterBuiltInProviders()
    {
        RegisterProvider("openai", (modelId, apiKey, apiEndpoint) =>
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException($"API Key is missing for provider 'openai'. Please configure it in Settings.");
            var actualKey = apiKey;
            // Retry ownership belongs to ChatService (or ResilientChatClient for other callers).
            // Disable SDK retries so application budgets do not multiply underneath that layer.
            var clientOptions = new OpenAIClientOptions { NetworkTimeout = TimeSpan.FromSeconds(200), RetryPolicy = new ClientRetryPolicy(0) };
            if (!string.IsNullOrEmpty(apiEndpoint))
            {
                clientOptions.Endpoint = new Uri(apiEndpoint);
            }
            var client = new OpenAIClient(new ApiKeyCredential(actualKey), clientOptions);
            // MEAI 10.x: OpenAIChatClient is internal; use the AsIChatClient() extension method instead.
            return client.GetChatClient(modelId).AsIChatClient();
        });

        RegisterProvider("azureopenai", (modelId, apiKey, apiEndpoint) =>
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException($"API Key is missing for provider 'azureopenai'. Please configure it in Settings.");
            var actualKey = apiKey;
            var endpoint = apiEndpoint ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? "https://api.cognitive.microsoft.com";
            var options = new AzureOpenAIClientOptions { NetworkTimeout = TimeSpan.FromSeconds(200), RetryPolicy = new ClientRetryPolicy(0) };
            var client = new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(actualKey), options);
            // MEAI 10.x: OpenAIChatClient is internal; use the AsIChatClient() extension method instead.
            return client.GetChatClient(modelId).AsIChatClient();
        });

        RegisterProvider("openrouter", (modelId, apiKey, apiEndpoint) =>
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException($"API Key is missing for provider 'openrouter'. Please configure it in Settings.");
            var actualKey = apiKey;
            var endpoint = apiEndpoint ?? "https://openrouter.ai/api/v1";
            var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(endpoint), NetworkTimeout = TimeSpan.FromSeconds(200), RetryPolicy = new ClientRetryPolicy(0) };
            clientOptions.AddPolicy(new OpenRouterHeadersPolicy("ApexCode", "https://github.com/kushalMeharwade/ApexCode"), PipelinePosition.PerCall);
            var client = new OpenAIClient(new ApiKeyCredential(actualKey), clientOptions);
            // MEAI 10.x: OpenAIChatClient is internal; use the AsIChatClient() extension method instead.
            return client.GetChatClient(modelId).AsIChatClient();
        });

        RegisterProvider("ollama", (modelId, apiKey, apiEndpoint) =>
        {
            var endpoint = apiEndpoint ?? Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT") ?? "http://localhost:11434";
            var client = new OllamaChatClient(new Uri(endpoint), modelId);
            return client;
        });
    }
}

public class OpenRouterHeadersPolicy : PipelinePolicy
{
    private readonly string _appName;
    private readonly string _siteUrl;

    public OpenRouterHeadersPolicy(string appName, string siteUrl)
    {
        _appName = appName;
        _siteUrl = siteUrl;
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        message.Request.Headers.Set("HTTP-Referer", _siteUrl);
        message.Request.Headers.Set("X-OpenRouter-Title", _appName);
        message.Request.Headers.Set("X-Title", _appName);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        message.Request.Headers.Set("HTTP-Referer", _siteUrl);
        message.Request.Headers.Set("X-OpenRouter-Title", _appName);
        message.Request.Headers.Set("X-Title", _appName);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }
}
