using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using AiAssistant.Tools.Services;
using AiAssistant.Core.Services;
using Microsoft.Extensions.AI;

namespace AiAssistant.Tools.Functions;

public class RunTestsFunction : IToolProvider
{
    private readonly IVisualStudioEnvironmentService _vsEnvService;
    private readonly IOutputLogger? _outputLogger;

    public RunTestsFunction(IVisualStudioEnvironmentService vsEnvService, IOutputLogger? outputLogger = null)
    {
        _vsEnvService = vsEnvService;
        _outputLogger = outputLogger;
    }

    [Description("Runs unit tests via dotnet test and returns structured output.")]
    public AIFunction CreateFunction() => new RunTestsCustomFunction(_vsEnvService, _outputLogger);

    private class RunTestsCustomFunction : CustomAIFunction
    {
        private readonly IVisualStudioEnvironmentService _vsEnvService;
        private readonly IOutputLogger? _outputLogger;

        public RunTestsCustomFunction(IVisualStudioEnvironmentService vsEnvService, IOutputLogger? outputLogger)
            : base("run_tests",
                   "Runs unit tests for the solution or a specific project using 'dotnet test'. Parses and returns structured output highlighting failures.",
                   @"{
                        ""type"": ""object"",
                        ""properties"": {
                            ""projectPath"": { ""type"": ""string"", ""description"": ""Optional. Path to a specific project file to test, relative to workspace root. Use forward slashes. If omitted, tests the whole solution."" },
                            ""filter"": { ""type"": ""string"", ""description"": ""Optional. Filter expression for dotnet test (e.g., 'FullyQualifiedName~MyNamespace.MyClass')."" }
                        }
                   }")
        {
            _vsEnvService = vsEnvService;
            _outputLogger = outputLogger;
        }

        protected override async Task<object?> InvokeCoreImplAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
        {
            _outputLogger?.Log(LogCategory.Tool, $"► {Name}()");

            try
            {
                var workspaceRoot = await _vsEnvService.GetWorkspaceRootAsync(cancellationToken);
                if (string.IsNullOrEmpty(workspaceRoot))
                {
                    return "Error: No workspace is open.";
                }

                string targetPath = workspaceRoot;
                if (arguments.TryGetValue("projectPath", out var ppObj) && ppObj?.ToString() is string projectPath && !string.IsNullOrWhiteSpace(projectPath))
                {
                    string absPath;
                    try
                    {
                        absPath = WorkspacePathResolver.ResolveWorkspacePath(projectPath, workspaceRoot);
                    }
                    catch (Exception ex)
                    {
                        return $"Error: projectPath resolution failed or traversal denied. {ex.Message}";
                    }
                    if (!File.Exists(absPath) && !Directory.Exists(absPath))
                    {
                        return $"Error: projectPath '{projectPath}' not found.";
                    }
                    targetPath = absPath;
                }

                string filterArgs = "";
                if (arguments.TryGetValue("filter", out var fObj) && fObj?.ToString() is string filter && !string.IsNullOrWhiteSpace(filter))
                {
                    // Basic escaping for powershell/cmd
                    filterArgs = $" --filter \"{filter.Replace("\"", "\\\"")}\"";
                }

                // We run `dotnet test` with verbosity normal and logger set to console
                string argumentsStr = $"test \"{targetPath}\"{filterArgs} --verbosity normal";

                var processStartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = argumentsStr,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = workspaceRoot
                };

                _outputLogger?.Log(LogCategory.Tool, $"Running: dotnet {argumentsStr}");

                using var process = Process.Start(processStartInfo);
                if (process == null) return "Error: Failed to start dotnet test process.";

                // Wait up to 2 minutes for tests
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                
                if (!process.WaitForExit(120000))
                {
                    try { process.Kill(); } catch { }
                    return $"Error: Test execution timed out after 2 minutes.";
                }

                var output = await outputTask;
                var error = await errorTask;
                var result = string.IsNullOrWhiteSpace(error) ? output : $"{output}\n[STDERR]\n{error}";
                if (string.IsNullOrWhiteSpace(result)) result = "Test run completed with no output.";

                // Extract a summary of failures for the AI to parse easily
                var lines = result.Split('\n');
                var failureSummary = new System.Text.StringBuilder();
                bool inFailedTest = false;
                
                foreach (var line in lines)
                {
                    var trimmed = line.TrimEnd();
                    if (trimmed.StartsWith("Failed "))
                    {
                        failureSummary.AppendLine(trimmed);
                        inFailedTest = true;
                    }
                    else if (trimmed.StartsWith("Passed ") || trimmed.StartsWith("Skipped "))
                    {
                        inFailedTest = false;
                    }
                    else if (inFailedTest && !string.IsNullOrWhiteSpace(trimmed))
                    {
                        failureSummary.AppendLine(trimmed);
                    }
                    else if (trimmed.StartsWith("Test Run ") || trimmed.StartsWith("Total tests:"))
                    {
                        failureSummary.AppendLine(trimmed);
                        inFailedTest = false;
                    }
                }

                string finalOutput = $"=== Test Summary ===\n{failureSummary}\n\n=== Full Output ===\n{result}";
                if (finalOutput.Length > 8000)
                {
                    finalOutput = finalOutput.Substring(0, 8000) + "\n... [Output truncated due to length]";
                }

                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → Process exited with code {process.ExitCode}");
                return finalOutput;
            }
            catch (Exception ex)
            {
                var errMsg = $"Error running tests: {ex.Message}";
                _outputLogger?.Log(LogCategory.Tool, $"◄ {Name} → {errMsg}");
                return errMsg;
            }
        }
    }
}


