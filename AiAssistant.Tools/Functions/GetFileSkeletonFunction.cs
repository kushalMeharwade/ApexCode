using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Engine.SkeletonEngine;

namespace AiAssistant.Tools.Functions;

public sealed class GetFileSkeletonFunction : CustomAIFunction
{
    private const string FunctionName = "get_file_skeleton";
    private const string FunctionDescription = "Generates a structural skeleton (outline of classes, methods, properties, etc.) of a given file. Useful to understand the schema of a file without reading its entire content.";
    private const string FunctionSchema = """
    {
        "type": "object",
        "properties": {
            "filePath": {
                "type": "string",
                "description": "Path to the target file, relative to workspace root. Use forward slashes."
            }
        },
        "required": ["filePath"]
    }
    """;

    private readonly AiAssistant.Core.Services.IVisualStudioEnvironmentService _vsEnvService;

    public GetFileSkeletonFunction(AiAssistant.Core.Services.IVisualStudioEnvironmentService vsEnvService) 
        : base(FunctionName, FunctionDescription, FunctionSchema)
    {
        _vsEnvService = vsEnvService;
    }

    protected override async Task<object?> InvokeCoreImplAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetValue("filePath", out var filePathObj) || filePathObj is not System.Text.Json.JsonElement je || je.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            // Fallback for simple string if not wrapped in JsonElement
            if (filePathObj is string s)
            {
                return await ProcessFilePathAsync(s, cancellationToken);
            }
            return "Error: filePath is required and must be a string.";
        }
        
        return await ProcessFilePathAsync(je.GetString(), cancellationToken);
    }

    private async Task<object> ProcessFilePathAsync(string filePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "Error: filePath is empty.";
        }

        string absolutePath;
        try
        {
            var workspaceRoot = await _vsEnvService.GetWorkspaceRootAsync(cancellationToken);
            absolutePath = WorkspacePathResolver.ResolveWorkspacePath(filePath, workspaceRoot);
        }
        catch (Exception ex)
        {
            return $"Error regarding {filePath}: Path resolution failed or traversal denied. {ex.Message}";
        }

        if (!File.Exists(absolutePath))
        {
            return $"Error: File not found at {absolutePath}";
        }

        try
        {
            // net472 does not have File.ReadAllTextAsync natively
            var content = File.ReadAllText(absolutePath);
            
            // Pass ignoreLimits = true to get the untruncated schema of the file
            var result = ParserRouter.Parse(absolutePath, content, int.MaxValue, ignoreLimits: true);

            return result.Outline;
        }
        catch (Exception ex)
        {
            return $"Error generating file skeleton: {ex.Message}";
        }
    }
}


