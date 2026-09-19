namespace AiAssistant.Core.Services;

/// <summary>Database tools share one executor and enforce read-only access in every agent mode.</summary>
public interface IDatabaseToolExecutor
{
    Task<string> GetSchemaAsync(string? tableName = null, CancellationToken cancellationToken = default);
    Task<string> ExecuteQueryAsync(string queryString, CancellationToken cancellationToken = default);
}
