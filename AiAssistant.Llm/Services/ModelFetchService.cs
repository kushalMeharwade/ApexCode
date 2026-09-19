using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Services;

namespace AiAssistant.Llm.Services;

public class ModelFetchService : IModelFetchService
{
    private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };
    private readonly IOutputLogger? _logger;
    private readonly AiAssistant.Storage.Repositories.IModelCacheRepository? _modelCacheRepo;

    public ModelFetchService(IOutputLogger? logger = null, AiAssistant.Storage.Repositories.IModelCacheRepository? modelCacheRepo = null)
    {
        _logger = logger;
        _modelCacheRepo = modelCacheRepo;
    }

    public async Task<IReadOnlyList<ModelInfo>> FetchModelsAsync(string providerType, string apiKey, string? apiEndpoint = null, string? modelFetchEndpoint = null, CancellationToken cancellationToken = default)
    {
        var models = new List<ModelInfo>();
        try
        {
            var endpoint = modelFetchEndpoint;
            if (string.IsNullOrEmpty(endpoint))
            {
                // Fallbacks if not provided
                endpoint = providerType.ToLowerInvariant() switch
                {
                    "openrouter" => "https://openrouter.ai/api/v1/models",
                    "ollama" => "http://localhost:11434/api/tags",
                    "azureopenai" => string.IsNullOrEmpty(apiEndpoint) ? null : $"{apiEndpoint?.TrimEnd('/')}/openai/deployments?api-version=2024-02-01",
                    _ => string.IsNullOrEmpty(apiEndpoint) ? "https://api.openai.com/v1/models" : $"{apiEndpoint?.TrimEnd('/')}/models"
                };
            }

            if (string.IsNullOrEmpty(endpoint)) 
            {
                _logger?.Log($"[ModelFetchService] Endpoint is empty after fallback for provider '{providerType}'. Aborting fetch.");
                return models;
            }

            _logger?.Log($"[ModelFetchService] Fetching models from '{endpoint}' for provider '{providerType}'");
            
            int maxRetries = 3;
            HttpResponseMessage? response = null;
            string content = string.Empty;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                    
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        if (providerType.ToLowerInvariant() == "azureopenai")
                            request.Headers.Add("api-key", apiKey);
                        else
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    }
                    request.Headers.UserAgent.ParseAdd("AiAssistant/1.0");

                    response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    _logger?.Log($"[ModelFetchService] Received response on attempt {attempt}: {(int)response.StatusCode} {response.ReasonPhrase}");
                    
                    if (response.IsSuccessStatusCode)
                    {
                        content = await response.Content.ReadAsStringAsync();
                        break;
                    }
                    else
                    {
                        var errContent = await response.Content.ReadAsStringAsync();
                        if (attempt < maxRetries)
                        {
                            _logger?.Log($"[ModelFetchService] HTTP Request failed on attempt {attempt}. Content: {errContent}. Retrying in 2 seconds...");
                            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                        }
                        else
                        {
                            _logger?.Log($"[ModelFetchService] HTTP Request failed on final attempt {attempt}. Content: {errContent}");
                            return models;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger?.Log($"[ModelFetchService] Request was cancelled by the user.");
                    return models;
                }
                catch (Exception ex)
                {
                    _logger?.Log($"[ModelFetchService] Exception during FetchModelsAsync attempt {attempt}: {ex.Message}");
                    if (attempt == maxRetries)
                    {
                        throw;
                    }
                    _logger?.Log($"[ModelFetchService] Retrying in 2 seconds...");
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
            }
            
            if (response != null && response.IsSuccessStatusCode)
            {
                _logger?.Log($"[ModelFetchService] Response content length: {content.Length} characters.");
                var json = JsonDocument.Parse(content);
                
                // Generic Parsing
                if (json.RootElement.TryGetProperty("data", out var data))
                {
                    foreach (var model in data.EnumerateArray())
                    {
                        try
                        {
                            var id = "";
                            if (model.TryGetProperty("id", out var idProp))
                                id = idProp.ValueKind == JsonValueKind.String ? (idProp.GetString() ?? "") : idProp.ToString();
                                
                            var name = id;
                            if (model.TryGetProperty("name", out var nameProp))
                                name = nameProp.ValueKind == JsonValueKind.String ? (nameProp.GetString() ?? "") : nameProp.ToString();
                                
                            var ctx = 128000;
                            if (model.TryGetProperty("context_length", out var ctxProp))
                            {
                                if (ctxProp.ValueKind == JsonValueKind.Number)
                                    ctx = ctxProp.GetInt32();
                                else if (ctxProp.ValueKind == JsonValueKind.String && int.TryParse(ctxProp.GetString(), out var parsed))
                                    ctx = parsed;
                            }
                            
                            decimal? inputPrice = null;
                            decimal? outputPrice = null;
                            bool isFree = false;

                            if (model.TryGetProperty("pricing", out var pricing))
                            {
                                try
                                {
                                    if (pricing.TryGetProperty("prompt", out var promptPrice))
                                    {
                                        if (promptPrice.ValueKind == JsonValueKind.Number)
                                            inputPrice = promptPrice.GetDecimal() * 1000000m;
                                        else if (promptPrice.ValueKind == JsonValueKind.String && decimal.TryParse(promptPrice.GetString(), out var p))
                                            inputPrice = p * 1000000m;
                                    }
                                    
                                    if (pricing.TryGetProperty("completion", out var completionPrice))
                                    {
                                        if (completionPrice.ValueKind == JsonValueKind.Number)
                                            outputPrice = completionPrice.GetDecimal() * 1000000m;
                                        else if (completionPrice.ValueKind == JsonValueKind.String && decimal.TryParse(completionPrice.GetString(), out var c))
                                            outputPrice = c * 1000000m;
                                    }
                                }
                                catch { /* Ignore pricing parse errors */ }
                                    
                                if (inputPrice == 0 && outputPrice == 0)
                                    isFree = true;
                            }

                            if (!string.IsNullOrEmpty(id))
                                models.Add(new ModelInfo(id, name, ctx, inputPrice, outputPrice, isFree));
                        }
                        catch { /* Ignore invalid model entries */ }
                    }
                }
                else if (json.RootElement.TryGetProperty("models", out var ollamaData))
                {
                    foreach (var model in ollamaData.EnumerateArray())
                    {
                        var name = model.GetProperty("name").GetString() ?? "";
                        models.Add(new ModelInfo(name, name, 8192, null, null, true));
                    }
                }
            }
        }
        catch (Exception ex) 
        { 
            _logger?.Log($"[ModelFetchService] Exception during FetchModelsAsync: {ex.Message}\n{ex.StackTrace}");
        }
        
        var orderedModels = models.OrderBy(m => m.DisplayName).ToList();
        
        if (_modelCacheRepo != null && orderedModels.Count > 0)
        {
            try
            {
                foreach (var m in orderedModels)
                {
                    await _modelCacheRepo.AddAsync(new AiAssistant.Storage.Models.ModelCache
                    {
                        ProviderId = providerType,
                        ModelId = m.ModelId,
                        DisplayName = m.DisplayName,
                        ContextWindowTokens = m.ContextWindowTokens,
                        CachedAt = DateTime.UtcNow,
                        ExpiresAt = DateTime.UtcNow.AddDays(7)
                    });
                }
                _logger?.Log($"[ModelFetchService] Persisted {orderedModels.Count} models to cache for provider '{providerType}'.");
            }
            catch (Exception ex)
            {
                _logger?.Log($"[ModelFetchService] Failed to persist models to cache: {ex.Message}");
            }
        }

        _logger?.Log($"[ModelFetchService] Returning {orderedModels.Count} parsed models.");
        return orderedModels;
    }
}

