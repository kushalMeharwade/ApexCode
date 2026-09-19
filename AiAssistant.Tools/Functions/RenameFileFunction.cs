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

public class RenameFileFunction : IToolProvider
{
    private readonly IVisualStudioEnvironmentService _vsEnvService;
    private readonly IApprovalService? _approvalService;
    private readonly IOutputLogger? _outputLogger;
    private readonly ISettingsService _settingsService;

    public RenameFileFunction(IVisualStudioEnvironmentService vsEnvService, ISettingsService settingsService, IApprovalService? approvalService = null, IOutputLogger? outputLogger = null)
    {
        _vsEnvService = vsEnvService;
        _settingsService = settingsService;
        _approvalService = approvalService;
        _outputLogger = outputLogger;
    }

    [Description("Renames a file in the workspace. Automatically checks for potential broken references.")]
    public AIFunction CreateFunction() => new RenameFileCustomFunction(_vsEnvService, _settingsService, _approvalService, _outputLogger);

    private class RenameFileCustomFunction : CustomAIFunction
    {
        private readonly IVisualStudioEnvironmentService _vsEnvService;
        private readonly ISettingsService _settingsService;
        private readonly IApprovalService? _approvalService;
        private readonly IOutputLogger? _outputLogger;

        public RenameFileCustomFunction(IVisualStudioEnvironmentService vsEnvService, ISettingsService settingsService, IApprovalService? approvalService, IOutputLogger? outputLogger)
            : base("rename_file",
                   "Renames a file in the workspace. Returns the number of files that might be referencing the old name, so you can update them.",
                   @"{
                        ""type"": ""object"",
                        ""properties"": {
                            ""oldFilePath"": { ""type"": ""string"", ""description"": ""The path to the existing file, relative to workspace root. Use forward slashes."" },
                            ""newFilePath"": { ""type"": ""string"", ""description"": ""The new path for the file, relative to workspace root. Use forward slashes."" }
                        },
                        ""required"": [""oldFilePath"", ""newFilePath""]
                   }")
        {
            _vsEnvService = vsEnvService;
            _settingsService = settingsService;
            _approvalService = approvalService;
            _outputLogger = outputLogger;
        }

        protected override async Task<object?> InvokeCoreImplAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
        {
            if (!arguments.TryGetValue("oldFilePath", out var ofObj) || ofObj?.ToString() is not string oldFilePath)
            {
                return "Error: oldFilePath argument is missing.";
            }
            if (!arguments.TryGetValue("newFilePath", out var nfObj) || nfObj?.ToString() is not string newFilePath)
            {
                return "Error: newFilePath argument is missing.";
            }

            _outputLogger?.Log(LogCategory.Tool, $"► {Name}({oldFilePath} -> {newFilePath})");

            var approvalMode = _settingsService.GetToolApprovalMode("rename_file");
            if (approvalMode == "denyall")
            {
                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → Error: Tool execution rejected due to Deny All setting.");
                return "Error: User rejected tool execution.";
            }

            if (_approvalService != null && approvalMode != "allowall")
            {
                var req = await _approvalService.RequestApprovalAsync(Name, $"Rename file: {oldFilePath} to {newFilePath}", arguments.ToDictionary(k => k.Key, v => (object?)v.Value));
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

                string oldAbsPath;
                string newAbsPath;
                try
                {
                    oldAbsPath = WorkspacePathResolver.ResolveWorkspacePath(oldFilePath, workspaceRoot);
                    newAbsPath = WorkspacePathResolver.ResolveWorkspacePath(newFilePath, workspaceRoot);
                }
                catch (Exception ex)
                {
                    return $"Error regarding {oldFilePath} or {newFilePath}: {ex.Message}";
                }

                if (!File.Exists(oldAbsPath))
                {
                    return $"Error: File not found ({oldAbsPath}).";
                }
                if (File.Exists(newAbsPath))
                {
                    return $"Error: Destination file already exists ({newAbsPath}).";
                }

                var oldFileName = Path.GetFileNameWithoutExtension(oldAbsPath);
                int referenceCount = 0;

                // Simple heuristic reference check
                try
                {
                    var allFiles = Directory.EnumerateFiles(workspaceRoot, "*.*", SearchOption.AllDirectories)
                        .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\") && !f.Contains("\\.git\\"));

                    foreach (var file in allFiles)
                    {
                        if (file.Equals(oldAbsPath, StringComparison.OrdinalIgnoreCase)) continue;
                        if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || 
                            file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || 
                            file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ||
                            file.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
                            file.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                        {
                            var content = File.ReadAllText(file);
                            if (content.IndexOf(oldFileName, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                referenceCount++;
                            }
                        }
                    }
                }
                catch { /* Ignore enumeration/read errors */ }

                // Create directory if it doesn't exist
                var dir = Path.GetDirectoryName(newAbsPath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.Move(oldAbsPath, newAbsPath);
                
                var successMsg = $"File: {WorkspacePathResolver.ToRelativePath(newAbsPath, workspaceRoot)}\nSuccessfully renamed from {WorkspacePathResolver.ToRelativePath(oldAbsPath, workspaceRoot)}.";
                if (referenceCount > 0)
                {
                    successMsg += $"\nWARNING: Found {referenceCount} other file(s) that reference '{oldFileName}'. You may need to update those references to prevent build breaks.";
                }

                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → {successMsg}");
                return successMsg;
            }
            catch (Exception ex)
            {
                var errMsg = $"Error renaming file: {ex.Message}";
                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → Error");
                return errMsg;
            }
        }
    }
}


