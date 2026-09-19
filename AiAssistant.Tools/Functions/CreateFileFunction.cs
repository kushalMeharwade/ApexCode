using Microsoft.Extensions.AI;
using System.ComponentModel;
using AiAssistant.Tools.Services;
using AiAssistant.Core.Services; // IApprovalService

namespace AiAssistant.Tools.Functions;

public class CreateFileFunction : IToolProvider
{
    private readonly IApprovalService? _approvalService;
    private readonly IOutputLogger? _outputLogger;
    private readonly ISettingsService _settingsService;
    private readonly IVisualStudioEnvironmentService _vsEnvService;

    public CreateFileFunction(
        ISettingsService settingsService,
        IVisualStudioEnvironmentService vsEnvService,
        IApprovalService? approvalService = null,
        IOutputLogger? outputLogger = null)
    {
        _settingsService = settingsService;
        _vsEnvService = vsEnvService;
        _approvalService = approvalService;
        _outputLogger = outputLogger;
    }

    [Description("Creates a new file with the specified content.")]
    public AIFunction CreateFunction() => new CreateFileCustomFunction(_settingsService, _vsEnvService, _approvalService, _outputLogger);

    private class CreateFileCustomFunction : CustomAIFunction
    {
        private readonly ISettingsService _settingsService;
        private readonly IVisualStudioEnvironmentService _vsEnvService;
        private readonly IApprovalService? _approvalService;
        private readonly IOutputLogger? _outputLogger;

        public CreateFileCustomFunction(
            ISettingsService settingsService,
            IVisualStudioEnvironmentService vsEnvService,
            IApprovalService? approvalService,
            IOutputLogger? outputLogger)
            : base("create_file",
                   "Creates a new file with the specified content.",
                   @"{
                        ""type"": ""object"",
                        ""properties"": {
                             ""filePath"": { ""type"": ""string"", ""description"": ""The path for the new file, relative to workspace root. Use forward slashes. Cannot be used to create or edit out-of-root linked files (files outside the workspace root, including csproj <Link> items that resolve outside the root)."" },
                            ""content"": { ""type"": ""string"", ""description"": ""The content to write to the file."" }
                        },
                        ""required"": [""filePath"", ""content""]
                   }")
        {
            _settingsService = settingsService;
            _vsEnvService = vsEnvService;
            _approvalService = approvalService;
            _outputLogger = outputLogger;
        }

        protected override async Task<object?> InvokeCoreImplAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
        {
            if (!arguments.TryGetValue("filePath", out var fpObj) || fpObj?.ToString() is not string filePath)
            {
                var receivedKeys = string.Join(", ", arguments.Keys);
                System.Diagnostics.Debug.WriteLine($"[CreateFile] Missing 'filePath'. Received keys: {receivedKeys}");
                return $"Error: filePath argument is missing. Received: {receivedKeys}";
            }

            if (!arguments.TryGetValue("content", out var contentObj) || contentObj?.ToString() is not string content)
            {
                var receivedKeys = string.Join(", ", arguments.Keys);
                System.Diagnostics.Debug.WriteLine($"[CreateFile] Missing 'content'. Received keys: {receivedKeys}");
                return $"Error: content argument is missing. Received: {receivedKeys}";
            }

            _outputLogger?.Log(LogCategory.Tool, $"► {Name}({filePath})", content);

            var approvalMode = _settingsService.GetToolApprovalMode("create_file");
            if (approvalMode == "denyall")
            {
                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → Error: Tool execution rejected due to Deny All setting.");
                return "Error: User rejected tool execution.";
            }

            if (_approvalService != null && approvalMode != "allowall")
            {
                var req = await _approvalService.RequestApprovalAsync(Name, "Creates a new file", arguments.ToDictionary(k => k.Key, v => (object?)v.Value));
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

            // ── Path jail (non-negotiable; approval mode cannot bypass this) ────────────
            var workspaceRoot = await _vsEnvService.GetWorkspaceRootAsync(cancellationToken);
            if (string.IsNullOrEmpty(workspaceRoot))
            {
                return "Error: No workspace is open.";
            }

            string absoluteFilePath;
            try
            {
                absoluteFilePath = WorkspacePathResolver.ResolveWorkspacePath(filePath, workspaceRoot);
            }
            catch (Exception ex)
            {
                var msg = $"Error regarding {filePath}: Path resolution failed or traversal denied. {ex.Message}";
                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → {msg}");
                return msg;
            }

            filePath = absoluteFilePath; // normalise to absolute

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            await AiAssistant.Storage.SafeFileWriter.WriteAllTextAsync(filePath, content);
            var resultMsg = $"File: {WorkspacePathResolver.ToRelativePath(filePath, workspaceRoot ?? string.Empty)}\nSuccessfully created.";
            _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → {resultMsg}");
            return resultMsg;
        }
    }
}


