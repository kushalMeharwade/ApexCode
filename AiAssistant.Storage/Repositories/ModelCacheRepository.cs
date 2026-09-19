using AiAssistant.Storage.Database;
using AiAssistant.Storage.Models;
using Dapper;
using System.Data.SQLite;
using System.Data;

namespace AiAssistant.Storage.Repositories;

public class ModelCacheRepository : IModelCacheRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public ModelCacheRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    private async Task<SQLiteConnection> GetOpenConnectionAsync()
    {
        var connection = await _connectionFactory.CreateConnectionAsync();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync();
        return connection;
    }

    public async Task<ModelCache?> GetByProviderAndModelIdAsync(string providerId, string modelId)
    {
        using var connection = await GetOpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<ModelCache>(
            "SELECT * FROM model_cache WHERE provider_id = @ProviderId AND model_id = @ModelId", new { ProviderId = providerId, ModelId = modelId });
    }

    public async Task<IEnumerable<ModelCache>> GetByProviderIdAsync(string providerId)
    {
        using var connection = await GetOpenConnectionAsync();
        return await connection.QueryAsync<ModelCache>(
            "SELECT * FROM model_cache WHERE provider_id = @ProviderId", new { ProviderId = providerId });
    }

    public async Task<ModelCache> AddAsync(ModelCache modelCache)
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            @"INSERT OR REPLACE INTO model_cache (id, provider_id, model_id, display_name, context_window_tokens, cached_at, expires_at) 
              VALUES (@Id, @ProviderId, @ModelId, @DisplayName, @ContextWindowTokens, @CachedAt, @ExpiresAt)",
            modelCache);
        return modelCache;
    }

    public async Task<IEnumerable<ModelCache>> GetAllAsync()
    {
        using var connection = await GetOpenConnectionAsync();
        return await connection.QueryAsync<ModelCache>("SELECT * FROM model_cache");
    }

    public async Task DeleteExpiredAsync()
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync("DELETE FROM model_cache WHERE expires_at < @Now", new { Now = DateTime.UtcNow });
    }

    public async Task RefreshFromApiAsync(string providerId, string apiKey)
    {
        if (!string.Equals(providerId, "openai", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            using var httpClient = new System.Net.Http.HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AiAssistant/1.0");

            var response = await httpClient.GetAsync("https://api.openai.com/v1/models");
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var json = System.Text.Json.JsonDocument.Parse(content);
                var data = json.RootElement.GetProperty("data");

                using var connection = await GetOpenConnectionAsync();
                foreach (var model in data.EnumerateArray())
                {
                    var id = model.GetProperty("id").GetString() ?? "";
                    if (!string.IsNullOrEmpty(id) && (id.StartsWith("gpt-") || id.StartsWith("o1") || id.StartsWith("o3")))
                    {
                        var cache = new ModelCache
                        {
                            Id = Guid.NewGuid().ToString(),
                            ProviderId = providerId,
                            ModelId = id,
                            DisplayName = id,
                            CachedAt = DateTime.UtcNow,
                            ExpiresAt = DateTime.UtcNow.AddDays(7)
                        };
                        await connection.ExecuteAsync(
                            @"INSERT OR REPLACE INTO model_cache (id, provider_id, model_id, display_name, context_window_tokens, cached_at, expires_at) 
                              VALUES (@Id, @ProviderId, @ModelId, @DisplayName, @ContextWindowTokens, @CachedAt, @ExpiresAt)",
                            cache);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] Failed to fetch models: {ex.Message}");
        }
    }
}
