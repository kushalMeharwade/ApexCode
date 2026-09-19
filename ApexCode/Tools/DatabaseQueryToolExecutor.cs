using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Services;
using AiAssistant.Storage.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ApexCode.Helpers;

namespace ApexCode.Tools;

/// <summary>Read-only tools bound to a captured connection and the shared SQL policy.</summary>
public class DatabaseQueryToolExecutor : IDatabaseToolExecutor
{
    public const int MaximumRows = 100;
    public const int MaximumCellCharacters = 2048;
    public const int MaximumResultCharacters = 65536;
    private readonly IDatabaseConnectionService _connectionService;
    private readonly ILogger<DatabaseQueryToolExecutor> _logger;
    private readonly IOutputLogger _outputLogger;
    private readonly IApprovalService? _approvalService;
    private readonly ISettingsService? _settingsService;

    public DatabaseQueryToolExecutor(IDatabaseConnectionService connectionService,
        ILogger<DatabaseQueryToolExecutor> logger, IOutputLogger outputLogger,
        IApprovalService? approvalService = null, ISettingsService? settingsService = null)
    {
        _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _outputLogger = outputLogger ?? throw new ArgumentNullException(nameof(outputLogger));
        _approvalService = approvalService;
        _settingsService = settingsService;
    }

    public async Task<string> GetSchemaAsync(string? tableName = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = _connectionService.ActiveConnection;
        var error = ValidateConnection(connection);
        if (error != null) return error;
        if (tableName?.Length > 258) return "Error: Table name is too long.";
        try
        {
            error = await AuthorizeAsync("get_database_schema", connection!,
                new { connection!.Id, Server = connection.ServerAddress, Database = connection.DatabaseName, TableName = tableName },
                cancellationToken);
            if (error != null) return error;
            var connectionString = await _connectionService.GetConnectionStringAsync(connection!);
            if (string.IsNullOrEmpty(connectionString)) return "Error: No database connection string is available.";
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(30));
            using var sqlConnection = await DatabaseConnectionHelper.OpenConnectionAsync(connectionString, _logger, budget.Token);
            var tables = new List<(int Id, string Schema, string Name)>();
            using (var cmd = new SqlCommand(@"
                SELECT TOP (101) t.object_id, s.name, t.name
                FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE @TableName IS NULL OR
                    (t.name = PARSENAME(@TableName, 1)
                     AND (PARSENAME(@TableName, 2) IS NULL OR s.name = PARSENAME(@TableName, 2))
                     AND PARSENAME(@TableName, 3) IS NULL AND PARSENAME(@TableName, 4) IS NULL)
                ORDER BY s.name, t.name", sqlConnection))
            {
                cmd.Parameters.Add("@TableName", SqlDbType.NVarChar, 258).Value =
                    string.IsNullOrWhiteSpace(tableName) ? DBNull.Value : (object)tableName!;
                using var reader = await cmd.ExecuteReaderAsync(budget.Token);
                while (await reader.ReadAsync(budget.Token))
                    tables.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
            }

            var sb = new StringBuilder();
            var truncated = tables.Count > 100;
            foreach (var table in tables.GetRange(0, Math.Min(tables.Count, 100)))
            {
                sb.AppendLine($"Table: {Quote(table.Schema)}.{Quote(table.Name)}");
                using var cmd = new SqlCommand(@"
                    SELECT c.name, ty.name, c.is_nullable, c.max_length, c.precision, c.scale
                    FROM sys.columns c JOIN sys.types ty ON c.user_type_id = ty.user_type_id
                    WHERE c.object_id = @ObjectId ORDER BY c.column_id;
                    SELECT c.name FROM sys.indexes i
                    JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                    WHERE i.object_id = @ObjectId AND i.is_primary_key = 1 ORDER BY ic.key_ordinal;
                    SELECT cp.name, sr.name, tr.name, cr.name
                    FROM sys.foreign_key_columns f
                    JOIN sys.columns cp ON cp.object_id = f.parent_object_id AND cp.column_id = f.parent_column_id
                    JOIN sys.tables tr ON tr.object_id = f.referenced_object_id
                    JOIN sys.schemas sr ON sr.schema_id = tr.schema_id
                    JOIN sys.columns cr ON cr.object_id = f.referenced_object_id AND cr.column_id = f.referenced_column_id
                    WHERE f.parent_object_id = @ObjectId ORDER BY f.constraint_object_id, f.constraint_column_id;",
                    sqlConnection);
                cmd.Parameters.Add("@ObjectId", SqlDbType.Int).Value = table.Id;
                using var reader = await cmd.ExecuteReaderAsync(budget.Token);
                while (await reader.ReadAsync(budget.Token))
                {
                    var type = reader.GetString(1);
                    var length = reader.GetInt16(3);
                    var suffix = type == "nvarchar" || type == "nchar"
                        ? $"({(length == -1 ? "max" : (length / 2).ToString(CultureInfo.InvariantCulture))})"
                        : type == "varchar" || type == "char" || type == "varbinary" || type == "binary"
                            ? $"({(length == -1 ? "max" : length.ToString(CultureInfo.InvariantCulture))})"
                            : type == "decimal" || type == "numeric" ? $"({reader.GetByte(4)},{reader.GetByte(5)})" : "";
                    sb.AppendLine($"  {Quote(reader.GetString(0))} {type}{suffix} {(reader.GetBoolean(2) ? "NULL" : "NOT NULL")}");
                    if (sb.Length > MaximumResultCharacters - 2048) { truncated = true; break; }
                }
                if (sb.Length > MaximumResultCharacters - 2048) break;
                await reader.NextResultAsync(budget.Token);
                var keys = new List<string>();
                while (await reader.ReadAsync(budget.Token)) keys.Add(Quote(reader.GetString(0)));
                if (keys.Count > 0) sb.AppendLine($"  PK: {string.Join(", ", keys)}");
                await reader.NextResultAsync(budget.Token);
                while (await reader.ReadAsync(budget.Token))
                {
                    sb.AppendLine($"  FK: {Quote(reader.GetString(0))} -> {Quote(reader.GetString(1))}.{Quote(reader.GetString(2))}.{Quote(reader.GetString(3))}");
                    if (sb.Length > MaximumResultCharacters - 2048) { truncated = true; break; }
                }
                if (sb.Length > MaximumResultCharacters - 2048) break;
                sb.AppendLine();
            }
            if (truncated) sb.AppendLine("[Schema truncated. Request a specific schema-qualified table.]");
            return sb.Length == 0 ? "No tables found." : sb.ToString().TrimEnd();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return "Error: Database schema read exceeded its 30-second budget."; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("Database schema read failed ({ErrorType}).", ex.GetType().Name);
            return "Error: Database schema read failed. Verify connectivity and metadata permissions.";
        }
    }

    public async Task<string> ExecuteQueryAsync(string queryString, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = _connectionService.ActiveConnection;
        var error = ValidateConnection(connection);
        if (error != null) return error;
        var rejection = DatabaseQueryPolicy.GetRejectionReason(queryString);
        if (rejection != null) return "Permission denied: Database tools are read-only. " + rejection;
        try
        {
            error = await AuthorizeAsync("execute_query", connection!,
                new { connection!.Id, Server = connection.ServerAddress, Database = connection.DatabaseName, Query = queryString },
                cancellationToken);
            if (error != null) return error;
            // Resolve the immutable snapshot the user approved, not the current active connection.
            var connectionString = await _connectionService.GetConnectionStringAsync(connection!);
            if (string.IsNullOrEmpty(connectionString)) return "Error: No database connection string is available.";
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(30));
            var elapsed = Stopwatch.StartNew();
            using var sqlConnection = await DatabaseConnectionHelper.OpenConnectionAsync(connectionString, _logger, budget.Token);
            using var cmd = new SqlCommand(queryString, sqlConnection) { CommandTimeout = 30 };
            using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, budget.Token);
            if (reader.FieldCount > 64)
            {
                cmd.Cancel();
                return "Error: Result exceeds 64 columns. Select fewer columns.";
            }

            var columns = new List<object>();
            for (var i = 0; i < reader.FieldCount; i++)
                columns.Add(new { name = reader.GetName(i), type = reader.GetDataTypeName(i) });
            var rows = new List<object?[]>();
            var truncated = false;
            var cellsTruncated = false;
            var remaining = MaximumResultCharacters - JsonSerializer.Serialize(columns).Length -
                JsonSerializer.Serialize(new { connection!.Id, connection.ServerAddress, connection.DatabaseName }).Length - 2048;
            while (await reader.ReadAsync(budget.Token))
            {
                if (rows.Count == MaximumRows) { truncated = true; break; }
                var values = new object?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    budget.Token.ThrowIfCancellationRequested();
                    if (await reader.IsDBNullAsync(i, budget.Token)) continue;
                    var fieldType = reader.GetFieldType(i);
                    if (fieldType == typeof(string))
                    {
                        var buffer = new char[MaximumCellCharacters + 1];
                        var read = (int)reader.GetChars(i, 0, buffer, 0, buffer.Length);
                        var length = Math.Min(read, MaximumCellCharacters);
                        if (read > length && length > 0 && char.IsHighSurrogate(buffer[length - 1])) length--;
                        values[i] = new string(buffer, 0, length);
                        cellsTruncated |= read > length;
                    }
                    else if (fieldType == typeof(byte[]))
                    {
                        var buffer = new byte[1025];
                        var read = (int)reader.GetBytes(i, 0, buffer, 0, buffer.Length);
                        values[i] = Convert.ToBase64String(buffer, 0, Math.Min(read, 1024));
                        cellsTruncated |= read > 1024;
                    }
                    else if (!fieldType.IsPrimitive && fieldType != typeof(decimal) && fieldType != typeof(DateTime) &&
                        fieldType != typeof(DateTimeOffset) && fieldType != typeof(TimeSpan) && fieldType != typeof(Guid))
                    {
                        values[i] = "[Unsupported SQL value; select an explicit scalar conversion.]";
                        cellsTruncated = true;
                    }
                    else values[i] = reader.GetValue(i);
                }
                var rowLength = JsonSerializer.Serialize(values).Length + 1;
                if (rowLength > remaining) { truncated = true; break; }
                rows.Add(values);
                remaining -= rowLength;
            }
            if (truncated) cmd.Cancel();
            var result = JsonSerializer.Serialize(new
            {
                connectionId = connection!.Id, server = connection.ServerAddress, database = connection.DatabaseName,
                columns, rows, returnedRowCount = rows.Count, truncated = truncated || cellsTruncated,
                rowsTruncated = truncated, cellsTruncated, durationMs = elapsed.ElapsedMilliseconds,
                rowLimit = MaximumRows, cellCharacterLimit = MaximumCellCharacters
            });
            // SQL literals and returned values may contain sensitive data. Log metadata only.
            await _outputLogger.LogAsync(LogCategory.Tool,
                $"execute_query: {rows.Count} row(s), truncated={truncated || cellsTruncated}, duration={elapsed.ElapsedMilliseconds}ms");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return "Error: Database query exceeded its 30-second budget."; }
        catch (OperationCanceledException) { throw; }
        catch (SqlException ex)
        {
            await _outputLogger.LogAsync(LogCategory.Tool, $"execute_query failed: SQL error {ex.Number}");
            return $"SQL Error {ex.Number}: Query failed. Check object names, permissions, types, and query cost.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Database query failed ({ErrorType}).", ex.GetType().Name);
            return "Error: Database query failed. Verify connectivity and database permissions.";
        }
    }

    private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    private static string? ValidateConnection(DatabaseConnection? connection)
    {
        if (connection == null) return "Error: No active database connection. Please select a connection from the toolbar.";
        if (!connection.IsEnabled) return "Permission denied: This database connection is disabled.";
        if (!Enum.IsDefined(typeof(PermissionLevel), connection.Permission))
            return "Permission denied: Unknown database permission level.";
        return null;
    }

    private async Task<string?> AuthorizeAsync(string toolName, DatabaseConnection connection,
        object parameters, CancellationToken cancellationToken)
    {
        var mode = _settingsService?.GetToolApprovalMode(toolName) ?? "allowall";
        if (mode == "denyall") return $"Permission denied: {toolName} is disabled in tool settings.";
        if (mode != "allowall" && mode != "prompt") return "Permission denied: Unknown database approval policy.";
        if (!connection.RequireApproval && mode == "allowall") return null;
        if (_approvalService == null) return "Permission denied: Approval is required but the approval service is unavailable.";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = await _approvalService.RequestApprovalAsync(toolName,
                $"Read database {connection.ServerAddress}/{connection.DatabaseName}", parameters);
            timeout.Token.ThrowIfCancellationRequested();
            if (request.IsRejected) return "User denied database access. Do not retry or bypass this denial.";
            if (request.IsApproved) return null;
            await foreach (var updated in _approvalService.WaitForApprovalAsync(request.Id, timeout.Token))
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (updated.IsRejected) return "User denied database access. Do not retry or bypass this denial.";
                if (updated.IsApproved) return null;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return "Permission denied: Database approval timed out."; }
        return "Permission denied: Database approval was not granted.";
    }

    public async Task<string> CheckConnectionAsync()
    {
        var connection = _connectionService.ActiveConnection;
        var error = ValidateConnection(connection);
        if (error != null) return "Reachable: false | " + error;
        try
        {
            var testTask = _connectionService.TestConnectionAsync(connection!);
            using var timeout = new CancellationTokenSource();
            if (await Task.WhenAny(testTask, Task.Delay(TimeSpan.FromSeconds(5), timeout.Token)) != testTask)
            {
                _ = testTask.ContinueWith(t => { _ = t.Exception; },
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                return "Reachable: false | Database connection check timed out.";
            }
            timeout.Cancel();
            var (success, _) = await testTask;
            return success
                ? $"Reachable: true | Connected to '{connection!.DatabaseName}' on '{connection.ServerAddress}'. Database tools are read-only."
                : "Reachable: false | Verify connectivity and database credentials.";
        }
        catch (Exception) { return "Reachable: false | Verify connectivity and database credentials."; }
    }
}
