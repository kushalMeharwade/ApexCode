using System;
using System.ComponentModel;
using System.Text;
using System.Threading.Tasks;
using AiAssistant.Core.Services;
using AiAssistant.Tools.Services;
using Microsoft.Extensions.AI;

namespace AiAssistant.Tools.Functions;

/// <summary>
/// Gets the database schema (tables, columns, data types, foreign keys) for the active database connection.
/// </summary>
public class GetDatabaseSchemaFunction : IToolProvider
{
    private readonly IDatabaseToolExecutor _executor;

    private readonly AiAssistant.Core.Services.IOutputLogger _outputLogger;

    public GetDatabaseSchemaFunction(AiAssistant.Core.Services.IOutputLogger outputLogger, IDatabaseToolExecutor executor)
    {
        _outputLogger = outputLogger;
        _executor = executor;
    }

    [Description("Gets the database schema (tables, columns, data types, foreign keys) for the active database connection.")]
    public AIFunction CreateFunction() => new GetDatabaseSchemaCustomFunction(_outputLogger, _executor);

    private class GetDatabaseSchemaCustomFunction : AiAssistant.Tools.Functions.CustomAIFunction
    {
        private readonly AiAssistant.Core.Services.IOutputLogger _outputLogger;
        private readonly IDatabaseToolExecutor _executor;

        public GetDatabaseSchemaCustomFunction(AiAssistant.Core.Services.IOutputLogger outputLogger, IDatabaseToolExecutor executor)
            : base("get_database_schema",
                   "Returns the database schema (tables, columns, types, foreign keys) in a compact format. Requires an active database connection.",
                   @"{
                        ""type"": ""object"",
                        ""properties"": {
                            ""tableName"": { ""type"": ""string"", ""description"": ""Optional table name or schema-qualified name (e.g. dbo.Users). Omit to discover tables, subject to output limits."" }
                        }
                   }")
        {
            _outputLogger = outputLogger;
            _executor = executor;
        }

        protected override async Task<object> InvokeCoreImplAsync(System.Collections.Generic.IReadOnlyDictionary<string, object> arguments, System.Threading.CancellationToken cancellationToken)
        {
            string? tableName = null;
            if (arguments.TryGetValue("tableName", out var tnObj) && tnObj?.ToString() is string tn)
                tableName = tn;

            await _outputLogger.LogAsync(AiAssistant.Core.Services.LogCategory.Tool, $"► get_database_schema(tableName=\"{tableName ?? "null"}\")");

            try
            {
                var result = await _executor.GetSchemaAsync(tableName, cancellationToken);
                _outputLogger.Log(AiAssistant.Core.Services.LogCategory.Tool, $"◄ get_database_schema → Schema returned ({result.Length} chars)");
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _outputLogger.Log(AiAssistant.Core.Services.LogCategory.Tool, $"◄ get_database_schema → Error: {ex.Message}");
                return $"Error reading database schema: {ex.Message}";
            }
        }
    }
}



