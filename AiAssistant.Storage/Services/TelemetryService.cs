using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AiAssistant.Storage.Database;
using System.Data.SQLite;

namespace AiAssistant.Storage.Services;

/// <summary>
/// Tracks feature usage telemetry in a local SQLite database.
/// All data stays local — nothing is sent externally.
/// </summary>
public interface ITelemetryService
{
    Task TrackEventAsync(string eventName, string? detail = null);
    Task TrackMessageSentAsync(string provider, string model);
    Task TrackToolExecutedAsync(string toolName, bool success);
    Task TrackPersonaUsedAsync(string personaName);
    Task TrackErrorAsync(string errorType, string message);
    Task<IReadOnlyList<TelemetryEntry>> GetRecentEventsAsync(int count = 100);
    Task<bool> IsEnabledAsync();
    Task SetEnabledAsync(bool enabled);
}

public class TelemetryEntry
{
    public long Id { get; set; }
    public string EventName { get; set; } = "";
    public string? Detail { get; set; }
    public DateTime Timestamp { get; set; }
}

public class TelemetryService : ITelemetryService
{
    private readonly IDbConnectionFactory _connectionFactory;
    private bool? _cachedEnabled;

    public TelemetryService(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task EnsureTableExistsAsync()
    {
        using var connection = await _connectionFactory.CreateConnectionAsync();
        await connection.OpenAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS telemetry_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_name TEXT NOT NULL,
                detail TEXT,
                timestamp TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS telemetry_settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            INSERT OR IGNORE INTO telemetry_settings (key, value) VALUES ('enabled', '1');
        ";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> IsEnabledAsync()
    {
        if (_cachedEnabled.HasValue) return _cachedEnabled.Value;

        using var connection = await _connectionFactory.CreateConnectionAsync();
        await connection.OpenAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM telemetry_settings WHERE key = 'enabled'";
        var result = await cmd.ExecuteScalarAsync();
        _cachedEnabled = result?.ToString() == "1";
        return _cachedEnabled.Value;
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        _cachedEnabled = enabled;
        using var connection = await _connectionFactory.CreateConnectionAsync();
        await connection.OpenAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO telemetry_settings (key, value) VALUES ('enabled', @val)";
        var param = cmd.CreateParameter();
        param.ParameterName = "@val";
        param.Value = enabled ? "1" : "0";
        cmd.Parameters.Add(param);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task TrackEventAsync(string eventName, string? detail = null)
    {
        if (!await IsEnabledAsync()) return;

        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            await connection.OpenAsync();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO telemetry_events (event_name, detail, timestamp) VALUES (@name, @detail, @ts)";
            var p1 = cmd.CreateParameter(); p1.ParameterName = "@name"; p1.Value = eventName; cmd.Parameters.Add(p1);
            var p2 = cmd.CreateParameter(); p2.ParameterName = "@detail"; p2.Value = (object?)detail ?? DBNull.Value; cmd.Parameters.Add(p2);
            var p3 = cmd.CreateParameter(); p3.ParameterName = "@ts"; p3.Value = DateTime.UtcNow.ToString("O"); cmd.Parameters.Add(p3);
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Telemetry must never crash the extension
        }
    }

    public Task TrackMessageSentAsync(string provider, string model)
        => TrackEventAsync("message_sent", $"{provider}/{model}");

    public Task TrackToolExecutedAsync(string toolName, bool success)
        => TrackEventAsync("tool_executed", $"{toolName}:{(success ? "success" : "failure")}");

    public Task TrackPersonaUsedAsync(string personaName)
        => TrackEventAsync("persona_used", personaName);

    public Task TrackErrorAsync(string errorType, string message)
        => TrackEventAsync("error", $"{errorType}: {message}");

    public async Task<IReadOnlyList<TelemetryEntry>> GetRecentEventsAsync(int count = 100)
    {
        var results = new List<TelemetryEntry>();
        using var connection = await _connectionFactory.CreateConnectionAsync();
        await connection.OpenAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, event_name, detail, timestamp FROM telemetry_events ORDER BY id DESC LIMIT @limit";
        var p = cmd.CreateParameter(); p.ParameterName = "@limit"; p.Value = count; cmd.Parameters.Add(p);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new TelemetryEntry
            {
                Id = reader.GetInt64(0),
                EventName = reader.GetString(1),
                Detail = reader.IsDBNull(2) ? null : reader.GetString(2),
                Timestamp = DateTime.Parse(reader.GetString(3))
            });
        }
        return results;
    }
}
