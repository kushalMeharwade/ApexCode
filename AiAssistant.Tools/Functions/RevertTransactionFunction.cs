using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AiAssistant.Core.Services;
using AiAssistant.Tools.Services;

namespace AiAssistant.Tools.Functions;

public class RevertTransactionFunction : IToolProvider
{
    private readonly IOutputLogger? _outputLogger;
    private readonly IVisualStudioEnvironmentService _vsEnvService;

    public RevertTransactionFunction(IVisualStudioEnvironmentService vsEnvService, IOutputLogger? outputLogger = null)
    {
        _vsEnvService = vsEnvService;
        _outputLogger = outputLogger;
    }

    [Description("Reverts a multi-file edit transaction to its original state using a transactionId.")]
    public AIFunction CreateFunction() => new RevertTransactionCustomFunction(_vsEnvService, _outputLogger);

    private class RevertTransactionCustomFunction : CustomAIFunction
    {
        private readonly IOutputLogger? _outputLogger;
        private readonly IVisualStudioEnvironmentService _vsEnvService;

        public RevertTransactionCustomFunction(IVisualStudioEnvironmentService vsEnvService, IOutputLogger? outputLogger)
            : base("revert_transaction",
                   "Reverts a multi-file edit transaction to its original state using the given transactionId.",
                   @"{
                        ""type"": ""object"",
                        ""properties"": {
                            ""transactionId"": { ""type"": ""string"", ""description"": ""The transaction ID returned by a previous tool call."" }
                        },
                        ""required"": [""transactionId""]
                   }")
        {
            _vsEnvService = vsEnvService;
            _outputLogger = outputLogger;
        }

        protected override async Task<object?> InvokeCoreImplAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
        {
            var transactionId = arguments["transactionId"]?.ToString();
            if (string.IsNullOrWhiteSpace(transactionId))
                return "Error: transactionId is required.";

            _outputLogger?.Log(LogCategory.Tool, $"► {Name}({transactionId})");

            var workspaceRoot = await _vsEnvService.GetWorkspaceRootAsync(cancellationToken);
            if (string.IsNullOrEmpty(workspaceRoot))
                return "Error: Cannot determine workspace root.";

            var transactionDir = Path.Combine(workspaceRoot, ".vs", "agent_transactions", transactionId);
            if (!Directory.Exists(transactionDir))
                return $"Error: Transaction {transactionId} not found or already deleted.";

            var manifestPath = Path.Combine(transactionDir, "manifest.json");
            if (!File.Exists(manifestPath))
                return $"Error: Transaction manifest missing for {transactionId}.";

            try
            {
                var manifestJson = File.ReadAllText(manifestPath);
                using var doc = JsonDocument.Parse(manifestJson);
                var restoredFiles = new List<string>();

                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    var originalPath = element.GetProperty("originalPath").GetString();
                    var snapshotFile = element.GetProperty("snapshotFile").GetString();

                    if (!string.IsNullOrEmpty(originalPath) && !string.IsNullOrEmpty(snapshotFile))
                    {
                        var snapshotPath = Path.Combine(transactionDir, snapshotFile);
                        if (File.Exists(snapshotPath))
                        {
                            var content = File.ReadAllText(snapshotPath);
                            await AiAssistant.Storage.SafeFileWriter.WriteAllTextAsync(originalPath, content);
                            restoredFiles.Add(originalPath);
                        }
                    }
                }

                Directory.Delete(transactionDir, true);
                var msg = $"Successfully reverted transaction {transactionId}. Restored {restoredFiles.Count} files.";
                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → {msg}");
                return msg;
            }
            catch (System.Exception ex)
            {
                var msg = $"Error reverting transaction: {ex.Message}";
                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → {msg}");
                return msg;
            }
        }
    }
}


