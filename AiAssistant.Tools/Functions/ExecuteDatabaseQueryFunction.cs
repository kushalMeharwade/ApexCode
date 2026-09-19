using System;
using System.ComponentModel;
using System.Threading.Tasks;
using AiAssistant.Core.Services;
using AiAssistant.Tools.Services;
using Microsoft.Extensions.AI;

namespace AiAssistant.Tools.Functions;

/// <summary>
/// Executes a database query against the active database connection.
/// </summary>
public class ExecuteDatabaseQueryFunction : IToolProvider
{
    private readonly IDatabaseToolExecutor _executor;
    private readonly AiAssistant.Core.Services.IOutputLogger _outputLogger;

    public ExecuteDatabaseQueryFunction(AiAssistant.Core.Services.IOutputLogger outputLogger, IDatabaseToolExecutor executor)
    {
        _outputLogger = outputLogger;
        _executor = executor;
    }

    [Description("Executes one validated read-only SELECT query in Plan or Act mode. Returns bounded JSON results with truncation metadata.")]
    public AIFunction CreateFunction() => new ExecuteDatabaseQueryCustomFunction(_outputLogger, _executor);

    private class ExecuteDatabaseQueryCustomFunction : AiAssistant.Tools.Functions.CustomAIFunction
    {
        private readonly AiAssistant.Core.Services.IOutputLogger _outputLogger;
        private readonly IDatabaseToolExecutor _executor;

        public ExecuteDatabaseQueryCustomFunction(AiAssistant.Core.Services.IOutputLogger outputLogger, IDatabaseToolExecutor executor)
            : base("execute_query",
                   "Executes one read-only SQL Server SELECT, optionally with a CTE, in Plan or Act mode. Writes and procedures are always blocked. Returns JSON with columns, rows, database identity and truncation flags; at most 100 rows with bounded cells and output size. Connection approval settings still apply.",
                   @"{
                        ""type"": ""object"",
                        ""properties"": {
                            ""queryString"": { ""type"": ""string"", ""description"": ""The exact SQL query string to execute (e.g., SELECT * FROM Users)"" }
                        },
                        ""required"": [""queryString""]
                   }")
        {
            _outputLogger = outputLogger;
            _executor = executor;
        }

        protected override async Task<object> InvokeCoreImplAsync(System.Collections.Generic.IReadOnlyDictionary<string, object> arguments, System.Threading.CancellationToken cancellationToken)
        {
            if (!arguments.TryGetValue("queryString", out var qsObj) || qsObj?.ToString() is not string queryString || string.IsNullOrWhiteSpace(queryString))
            {
                return "Error: Missing or empty 'queryString' parameter.";
            }

            try
            {
                var result = await _executor.ExecuteQueryAsync(queryString, cancellationToken);
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _outputLogger.Log(AiAssistant.Core.Services.LogCategory.Tool, $"◄ execute_query → Error: {ex.Message}");
                return $"Error executing database query: {ex.Message}";
            }
        }
    }
}


