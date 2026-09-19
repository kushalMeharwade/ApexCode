using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using AiAssistant.Tools.Services;
using AiAssistant.Core.Services;
using Microsoft.Extensions.AI;

namespace AiAssistant.Tools.Functions;

public class DeleteFileFunction : IToolProvider
{
    private readonly IVisualStudioEnvironmentService _vsEnvService;
    private readonly IApprovalService? _approvalService;
    private readonly IOutputLogger? _outputLogger;
    private readonly ISettingsService _settingsService;

    public DeleteFileFunction(IVisualStudioEnvironmentService vsEnvService, ISettingsService settingsService, IApprovalService? approvalService = null, IOutputLogger? outputLogger = null)
    {
        _vsEnvService = vsEnvService;
        _settingsService = settingsService;
        _approvalService = approvalService;
        _outputLogger = outputLogger;
    }

    [Description("Deletes a file from the workspace. This is a destructive operation.")]
    public AIFunction CreateFunction() => new DeleteFileCustomFunction(_vsEnvService, _settingsService, _approvalService, _outputLogger);

    private class DeleteFileCustomFunction : CustomAIFunction
    {
        private readonly IVisualStudioEnvironmentService _vsEnvService;
        private readonly ISettingsService _settingsService;
        private readonly IApprovalService? _approvalService;
        private readonly IOutputLogger? _outputLogger;

        public DeleteFileCustomFunction(IVisualStudioEnvironmentService vsEnvService, ISettingsService settingsService, IApprovalService? approvalService, IOutputLogger? outputLogger)
            : base("delete_file",
                   "Deletes a file from the workspace. This is a destructive operation.",
                   @"{
                        ""type"": ""object"",
                        ""properties"": {
                            ""filePath"": { ""type"": ""string"", ""description"": ""The path to the file to delete, relative to workspace root. Use forward slashes."" }
                        },
                        ""required"": [""filePath""]
                   }")
        {
            _vsEnvService = vsEnvService;
            _settingsService = settingsService;
            _approvalService = approvalService;
            _outputLogger = outputLogger;
        }

        protected override async Task<object?> InvokeCoreImplAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
        {
            if (!arguments.TryGetValue("filePath", out var fpObj) || fpObj?.ToString() is not string filePath)
            {
                return "Error: filePath argument is missing.";
            }

            _outputLogger?.Log(LogCategory.Tool, $"► {Name}({filePath})");

            var approvalMode = _settingsService.GetToolApprovalMode("delete_file");
            if (approvalMode == "denyall")
            {
                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → Error: Tool execution rejected due to Deny All setting.");
                return "Error: User rejected tool execution.";
            }

            if (_approvalService != null && approvalMode != "allowall")
            {
                var req = await _approvalService.RequestApprovalAsync(Name, $"Delete file: {filePath}", arguments.ToDictionary(k => k.Key, v => (object?)v.Value));
                await foreach (var status in _approvalService.WaitForApprovalAsync(req.Id, cancellationToken))
                {
                    if (status.IsRejected)
                    {
                        _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → Error: User rejected tool execution.");
                        return "USER REJECTED THIS ACTION. CRITICAL INSTRUCTION: Do NOT retry this tool. Do NOT attempt alternative commands. You MUST STOP and ask the user for further instructions.";
                    }
                    if (status.IsApproved) break;
                }
            }

            try
            {
                var workspaceRoot = await _vsEnvService.GetWorkspaceRootAsync(cancellationToken);
                if (string.IsNullOrEmpty(workspaceRoot))
                {
                    return "Error: No workspace is open.";
                }

                string absolutePath;
                try
                {
                    absolutePath = WorkspacePathResolver.ResolveWorkspacePath(filePath, workspaceRoot);
                }
                catch (Exception ex)
                {
                    var jailMsg = $"Error regarding {filePath}: Path resolution failed or traversal denied. {ex.Message}";
                    _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → {jailMsg}");
                    return jailMsg;
                }

                if (!File.Exists(absolutePath))
                {
                    return $"Error: File not found ({absolutePath}).";
                }

                File.Delete(absolutePath);
                
                var successMsg = $"File: {WorkspacePathResolver.ToRelativePath(absolutePath, workspaceRoot)}\nSuccessfully deleted.";
                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → {successMsg}");
                return successMsg;
            }
            catch (Exception ex)
            {
                var errMsg = $"Error deleting file '{filePath}': {ex.Message}";
                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → Error");
                return errMsg;
            }
        }
    }
}


