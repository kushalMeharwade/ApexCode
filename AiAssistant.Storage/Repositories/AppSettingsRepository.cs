using System.Threading.Tasks;
using AiAssistant.Storage.Database;
using Dapper;

namespace AiAssistant.Storage.Repositories;

public class AppSettingsRepository : IAppSettingsRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public AppSettingsRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    private async Task<System.Data.SQLite.SQLiteConnection> GetOpenConnectionAsync()
    {
        var connection = await _connectionFactory.CreateConnectionAsync();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();
        return connection;
    }

    public async Task<string?> GetValueAsync(string key)
    {
        try
        {
            using var connection = await GetOpenConnectionAsync();
            return await connection.QuerySingleOrDefaultAsync<string>(
                "SELECT value FROM app_settings WHERE key = @Key",
                new { Key = key });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] AppSettingsRepository.GetValueAsync error for key '{key}': {ex.Message}");
            return null;
        }
    }

    public async Task SetValueAsync(string key, string value)
    {
        try
        {
            using var connection = await GetOpenConnectionAsync();
            await connection.ExecuteAsync(
                "INSERT INTO app_settings (key, value) VALUES (@Key, @Value) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                new { Key = key, Value = value });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] AppSettingsRepository.SetValueAsync error for key '{key}': {ex.Message}");
        }
    }
}
