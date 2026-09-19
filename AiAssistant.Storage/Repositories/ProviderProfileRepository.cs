using AiAssistant.Storage.Database;
using AiAssistant.Storage.Models;
using AiAssistant.Storage.Security;
using Dapper;
using System.Data.SQLite;
using System.Data;
using System.Linq;

namespace AiAssistant.Storage.Repositories;

public class ProviderProfileRepository : IProviderProfileRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public ProviderProfileRepository(IDbConnectionFactory connectionFactory)
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

    public async Task<ProviderProfile?> GetByIdAsync(string id)
    {
        try
        {
            using var connection = await GetOpenConnectionAsync();
            var profile = await connection.QuerySingleOrDefaultAsync<ProviderProfile>(
                "SELECT * FROM provider_profiles WHERE id = @Id", new { Id = id });
            if (profile != null)
            {
                profile = profile with { ApiKey = EncryptionHelper.Decrypt(profile.ApiKey) };
            }
            return profile;
        }
        catch (SQLiteException ex) when (ex.ErrorCode == 1) // no such table
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] Table provider_profiles not found in GetByIdAsync. Auto-initializing schema...");
            await AiAssistantDbContext.EnsureInitializedAsync(_connectionFactory);

            using var retryConn = await GetOpenConnectionAsync();
            var profile = await retryConn.QuerySingleOrDefaultAsync<ProviderProfile>(
                "SELECT * FROM provider_profiles WHERE id = @Id", new { Id = id });
            if (profile != null)
            {
                profile = profile with { ApiKey = EncryptionHelper.Decrypt(profile.ApiKey) };
            }
            return profile;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] Error in GetByIdAsync: {ex.Message}");
            return null;
        }
    }

    public async Task<IEnumerable<ProviderProfile>> GetAllAsync()
    {
        try
        {
            using var connection = await GetOpenConnectionAsync();
            var profiles = await connection.QueryAsync<ProviderProfile>("SELECT * FROM provider_profiles");
            var profilesList = profiles.Select(p => p with { ApiKey = EncryptionHelper.Decrypt(p.ApiKey) }).ToList();
            return profilesList;
        }
        catch (SQLiteException ex) when (ex.ErrorCode == 1) // no such table
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] Table provider_profiles not found in GetAllAsync. Auto-initializing schema...");
            await AiAssistantDbContext.EnsureInitializedAsync(_connectionFactory);

            using var retryConn = await GetOpenConnectionAsync();
            var profiles = await retryConn.QueryAsync<ProviderProfile>("SELECT * FROM provider_profiles");
            var profilesList = profiles.Select(p => p with { ApiKey = EncryptionHelper.Decrypt(p.ApiKey) }).ToList();
            return profilesList;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] Error in GetAllAsync: {ex.Message}");
            return Enumerable.Empty<ProviderProfile>();
        }
    }

    public async Task<ProviderProfile?> GetByNameAsync(string name)
    {
        try
        {
            using var connection = await GetOpenConnectionAsync();
            var profile = await connection.QuerySingleOrDefaultAsync<ProviderProfile>(
                "SELECT * FROM provider_profiles WHERE name = @Name", new { Name = name });
            if (profile != null)
            {
                profile = profile with { ApiKey = EncryptionHelper.Decrypt(profile.ApiKey) };
            }
            return profile;
        }
        catch (SQLiteException ex) when (ex.ErrorCode == 1) // no such table
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] Table provider_profiles not found in GetByNameAsync. Auto-initializing schema...");
            await AiAssistantDbContext.EnsureInitializedAsync(_connectionFactory);

            using var retryConn = await GetOpenConnectionAsync();
            var profile = await retryConn.QuerySingleOrDefaultAsync<ProviderProfile>(
                "SELECT * FROM provider_profiles WHERE name = @Name", new { Name = name });
            if (profile != null)
            {
                profile = profile with { ApiKey = EncryptionHelper.Decrypt(profile.ApiKey) };
            }
            return profile;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] Error in GetByNameAsync: {ex.Message}");
            return null;
        }
    }

    public async Task<ProviderProfile> AddAsync(ProviderProfile profile)
    {
        using var connection = await GetOpenConnectionAsync();
        
        var encryptedApiKey = EncryptionHelper.Encrypt(profile.ApiKey);
        
        await connection.ExecuteAsync(
            @"INSERT INTO provider_profiles (id, name, provider_type, api_endpoint, model_fetch_endpoint, api_key, default_model, context_window_tokens, is_enabled, is_built_in, logo_resource_key, built_in_id, created_at) 
              VALUES (@Id, @Name, @ProviderType, @ApiEndpoint, @ModelFetchEndpoint, @ApiKey, @DefaultModel, @ContextWindowTokens, @IsEnabled, @IsBuiltIn, @LogoResourceKey, @BuiltInId, @CreatedAt)",
            new {
                profile.Id, profile.Name, profile.ProviderType, profile.ApiEndpoint, profile.ModelFetchEndpoint,
                ApiKey = encryptedApiKey, profile.DefaultModel, profile.ContextWindowTokens, profile.IsEnabled, 
                profile.IsBuiltIn, profile.LogoResourceKey, profile.BuiltInId, profile.CreatedAt
            });
        return profile;
    }

    public async Task<ProviderProfile> UpdateAsync(ProviderProfile profile)
    {
        using var connection = await GetOpenConnectionAsync();
        
        var encryptedApiKey = EncryptionHelper.Encrypt(profile.ApiKey);
        
        await connection.ExecuteAsync(
            @"UPDATE provider_profiles SET name = @Name, provider_type = @ProviderType, api_endpoint = @ApiEndpoint, model_fetch_endpoint = @ModelFetchEndpoint, 
              api_key = @ApiKey, default_model = @DefaultModel, context_window_tokens = @ContextWindowTokens, is_enabled = @IsEnabled, is_built_in = @IsBuiltIn, logo_resource_key = @LogoResourceKey, built_in_id = @BuiltInId WHERE id = @Id",
            new {
                profile.Name, profile.ProviderType, profile.ApiEndpoint, profile.ModelFetchEndpoint,
                ApiKey = encryptedApiKey, profile.DefaultModel, profile.ContextWindowTokens, profile.IsEnabled, 
                profile.IsBuiltIn, profile.LogoResourceKey, profile.BuiltInId, profile.Id
            });
        return profile;
    }

    public async Task DeleteAsync(string id)
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync("DELETE FROM provider_profiles WHERE id = @Id", new { Id = id });
    }
}
