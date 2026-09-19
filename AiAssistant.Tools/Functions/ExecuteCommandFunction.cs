using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using AiAssistant.Tools.Services;
using AiAssistant.Core.Services;
using Microsoft.Extensions.AI;

namespace AiAssistant.Tools.Functions;

public class ExecuteCommandFunction : IToolProvider
{
    private readonly IVisualStudioEnvironmentService _vsEnvService;
    private readonly IApprovalService? _approvalService;
    private readonly IOutputLogger? _outputLogger;
    private readonly ISettingsService _settingsService;
    private readonly CommandSessionService _sessions;

    public ExecuteCommandFunction(IVisualStudioEnvironmentService vsEnvService, ISettingsService settingsService, IApprovalService? approvalService = null, IOutputLogger? outputLogger = null, CommandSessionService? sessions = null)
    {
        _vsEnvService = vsEnvService;
        _settingsService = settingsService;
        _approvalService = approvalService;
        _outputLogger = outputLogger;
        _sessions = sessions ?? new CommandSessionService();
    }

    [Description("Executes a command prompt or PowerShell command in the workspace directory. Returns a persistent session for long commands. Poll read_command_output until completion; running is not success. Use background=true only for servers and verify readiness separately. Do NOT use this to run tests; use run_tests instead.")]
    public AIFunction CreateFunction() => new ExecuteCommandCustomFunction(_vsEnvService, _settingsService, _approvalService, _outputLogger, _sessions);

    private class ExecuteCommandCustomFunction : CustomAIFunction
    {
        private readonly IVisualStudioEnvironmentService _vsEnvService;
        private readonly ISettingsService _settingsService;
        private readonly CommandSessionService _sessions;
        private readonly IApprovalService? _approvalService;
        private readonly IOutputLogger? _outputLogger;

        public ExecuteCommandCustomFunction(IVisualStudioEnvironmentService vsEnvService, ISettingsService settingsService, IApprovalService? approvalService, IOutputLogger? outputLogger, CommandSessionService sessions)
            : base("execute_command",
                   "Executes a command prompt or PowerShell command in the workspace directory. Returns a persistent session for long commands. Poll read_command_output until completion; running is not success. Use background=true only for servers and verify readiness separately. Do NOT use this to run tests; use run_tests instead.",
                   @"{
                        ""type"": ""object"",
                        ""properties"": {
                            ""command"": { ""type"": ""string"", ""description"": ""The command to execute (e.g. 'git status', 'dotnet build', 'npm run build')."" },
                            ""yield_time_ms"": { ""type"": ""integer"", ""minimum"": 1000, ""maximum"": 30000, ""description"": ""Initial wait, default 10000 ms. Does not limit process lifetime."" },
                            ""timeout_seconds"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 86400, ""description"": ""Execution limit; default 1200 for builds, 14400 for background servers."" },
                            ""background"": { ""type"": ""boolean"", ""description"": ""Use only for persistent servers, never to bypass build verification."" }
                        },
                        ""required"": [""command""]
                   }")
        {
            _vsEnvService = vsEnvService;
            _settingsService = settingsService;
            _approvalService = approvalService;
            _outputLogger = outputLogger;
            _sessions = sessions;
        }

        protected override async Task<object?> InvokeCoreImplAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
        {
            if (!arguments.TryGetValue("command", out var cmdObj) || cmdObj?.ToString() is not string command)
            {
                var receivedKeys = string.Join(", ", arguments.Keys);
                System.Diagnostics.Debug.WriteLine($"[ExecuteCommand] Missing 'command'. Received keys: {receivedKeys}");
                return $"Error: command argument is missing. Received: {receivedKeys}";
            }

            var cmd = command.Trim();
            if (cmd.Length == 0) return "Error: command cannot be empty.";
            _outputLogger?.Log(LogCategory.Tool, $"► {Name}({cmd})");

            var approvalMode = _settingsService.GetToolApprovalMode("execute_command");
            if (approvalMode == "denyall")
            {
                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → Error: Tool execution rejected due to Deny All setting.");
                return "Error: User rejected tool execution.";
            }

            var maliciousCommands = new[] { "rm -rf", "del ", "format ", "mklink", "powercfg", "reg " };
            var isMalicious = maliciousCommands.Any(m => cmd.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0);

            var whitelist = _settingsService.ExecuteCommandWhitelist ?? new List<string>();
            var isWhitelisted = !isMalicious && whitelist.Any(w => cmd.StartsWith(w, StringComparison.OrdinalIgnoreCase));

            if (_approvalService == null && (approvalMode != "allowall" || !isWhitelisted))
                return "Error: Approval is required but the approval service is unavailable.";

            if (_approvalService != null && (approvalMode != "allowall" || !isWhitelisted))
            {
                bool approved = false;
                var req = await _approvalService.RequestApprovalAsync(Name, $"Execute command: {cmd}", arguments.ToDictionary(k => k.Key, v => (object?)v.Value));
                await foreach (var status in _approvalService.WaitForApprovalAsync(req.Id, cancellationToken))
                {
                    if (status.IsRejected)
                    {
                        _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → Error: User rejected tool execution.");
                        return "USER REJECTED THIS ACTION. CRITICAL INSTRUCTION: Do NOT retry this tool. Do NOT attempt alternative commands. You MUST STOP and ask the user for further instructions.";
                    }
                    if (status.IsApproved) { approved = true; break; }
                }
                if (!approved) return "Error: Approval ended without an explicit decision.";
            }

            try
            {
                var workspacePath = await _vsEnvService.GetWorkspaceRootAsync(cancellationToken);
                if (string.IsNullOrEmpty(workspacePath))
                {
                    return "Error: No workspace is open.";
                }

                bool background = arguments.TryGetValue("background", out var bg) && bool.Parse(bg!.ToString()!);
                int wait = CommandToolArguments.Integer(arguments, "yield_time_ms", 10000, 1000, 30000);
                int timeout = CommandToolArguments.Integer(arguments, "timeout_seconds", background ? 14400 : 1200, 1, 86400);
                var id = _sessions.Start(cmd, workspacePath, background, timeout, cancellationToken);
                return await _sessions.ReadAsync(id, workspacePath, 0, wait, cancellationToken, waitForExit: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var errMsg = $"Error executing '{command}': {ex.Message}";
                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → Error");
                return errMsg;
            }
        }
    }
}


