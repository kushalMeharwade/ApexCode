using System;
using System.ComponentModel;
using System.Threading.Tasks;
using AiAssistant.Tools.Services;
using Microsoft.Extensions.AI;

namespace AiAssistant.Tools.Functions;

/// <summary>
/// Checks if the active database connection is reachable.
/// </summary>
public class CheckDatabaseConnectionFunction : IToolProvider
{
    public static Func<Task<string>>? ConnectionTester { get; set; }

    private readonly AiAssistant.Core.Services.IOutputLogger _outputLogger;

    public CheckDatabaseConnectionFunction(AiAssistant.Core.Services.IOutputLogger outputLogger)
    {
        _outputLogger = outputLogger;
    }

    [Description("Checks if the active database connection is reachable. Call this proactively before running any queries if you haven't verified connectivity recently, or if a prior query failed with a timeout or network error.")]
    public AIFunction CreateFunction() => new CheckDatabaseConnectionCustomFunction(_outputLogger);

    private class CheckDatabaseConnectionCustomFunction : AiAssistant.Tools.Functions.CustomAIFunction
    {
        private readonly AiAssistant.Core.Services.IOutputLogger _outputLogger;

        public CheckDatabaseConnectionCustomFunction(AiAssistant.Core.Services.IOutputLogger outputLogger)
            : base("check_database_connection",
                   "Checks if the active database connection is reachable. Call this proactively before running any queries if you haven't verified connectivity recently, or if a prior query failed with a timeout or network error.",
                   @"{
                        ""type"": ""object"",
                        ""properties"": {}
                   }")
        {
            _outputLogger = outputLogger;
        }

        protected override async Task<object?> InvokeCoreImplAsync(System.Collections.Generic.IReadOnlyDictionary<string, object?> arguments, System.Threading.CancellationToken cancellationToken)
        {
            await _outputLogger.LogAsync(AiAssistant.Core.Services.LogCategory.Tool, $"► check_database_connection()");

            if (ConnectionTester == null)
            {
                var error = "Reachable: false | ❌ Error: No database connection service available.";
                await _outputLogger.LogAsync(AiAssistant.Core.Services.LogCategory.Tool, $"◄ check_database_connection → {error}");
                return error;
            }

            try
            {
                var result = await ConnectionTester();
                await _outputLogger.LogAsync(AiAssistant.Core.Services.LogCategory.Tool, $"◄ check_database_connection → {result}");
                return result;
            }
            catch (Exception ex)
            {
                var error = $"Reachable: false | ❌ Error checking database connection: {ex.Message}";
                await _outputLogger.LogAsync(AiAssistant.Core.Services.LogCategory.Tool, $"◄ check_database_connection → {error}");
                return error;
            }
        }
    }
}


