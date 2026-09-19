using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AiAssistant.Core.Models;
using AiAssistant.Core.Services;
using AiAssistant.Engine.Services;
using Microsoft.Extensions.Logging;
using IChatClientFactory = AiAssistant.Llm.Services.IChatClientFactory;

namespace ApexCode.Services;

/// <summary>
/// LLM-based implementation of IErrorFixer.
/// Uses the ChatClientFactory to call the LLM with a fix prompt.
/// </summary>
public class LlmErrorFixer : IErrorFixer
{
    private readonly IChatClientFactory _clientFactory;
    private readonly ISystemPromptManager _promptManager;
    private readonly AiAssistant.Storage.Repositories.IProviderProfileRepository _providerRepo;
    private readonly ILogger<LlmErrorFixer> _logger;
    private readonly IFileBackupService _backupService;
    private readonly IVisualStudioEnvironmentService _vsEnvironment;

    public LlmErrorFixer(
        IChatClientFactory clientFactory,
        ISystemPromptManager promptManager,
        AiAssistant.Storage.Repositories.IProviderProfileRepository providerRepo,
        IFileBackupService backupService,
        IVisualStudioEnvironmentService vsEnvironment,
        ILogger<LlmErrorFixer> logger)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _promptManager = promptManager ?? throw new ArgumentNullException(nameof(promptManager));
        _providerRepo = providerRepo ?? throw new ArgumentNullException(nameof(providerRepo));
        _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
        _vsEnvironment = vsEnvironment ?? throw new ArgumentNullException(nameof(vsEnvironment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ErrorFixResult> FixErrorAsync(
        string filePath,
        string fileContent,
        string errorMessage,
        string projectPath,
        CancellationToken ct = default)
    {
        try
        {
            // Parse the error to extract structured information
            var parsedError = ParseError(errorMessage);

            // Build the fix prompt
            var prompt = BuildFixPrompt(filePath, fileContent, errorMessage, parsedError);

            // Get the default system prompt for code fixing
            var systemPrompt = await _promptManager.GetDefaultPromptAsync();
            var fixSystemPrompt = systemPrompt != null
                ? $"{systemPrompt.Content}\n\nYou are now in error-fixing mode. Given a compilation error, analyze the code and provide ONLY the corrected code. Do not explain your changes. Output only the fixed code."
                : "You are a C# code fixing assistant. Given a compilation error, analyze the code and provide ONLY the corrected code. Do not explain your changes. Output only the fixed code.";

            // Call the LLM
            var allProfiles = await _providerRepo.GetAllAsync();
            var profile = allProfiles.FirstOrDefault(p => p.IsEnabled);
            
            if (profile == null)
            {
                return new ErrorFixResult
                {
                    Success = false,
                    ErrorMessage = "No enabled provider profile found."
                };
            }

            var client = _clientFactory.CreateClient(profile.ProviderType, profile.DefaultModel ?? "gpt-4o", profile.ApiKey, profile.ApiEndpoint);
            var messages = new List<Microsoft.Extensions.AI.ChatMessage>
            {
                new(Microsoft.Extensions.AI.ChatRole.System, fixSystemPrompt),
                new(Microsoft.Extensions.AI.ChatRole.User, prompt)
            };

            var response = await client.GetResponseAsync(messages, cancellationToken: ct);
            var fixedCode = response.Text;

            if (string.IsNullOrWhiteSpace(fixedCode))
            {
                return new ErrorFixResult
                {
                    Success = false,
                    ErrorMessage = "LLM returned empty response"
                };
            }

            // Clean up the response — remove markdown code fences if present
            fixedCode = CleanCodeResponse(fixedCode);

            // Stage 1: Empty check
            if (string.IsNullOrWhiteSpace(fixedCode))
            {
                return new ErrorFixResult { Success = false, ErrorMessage = "LLM returned empty code" };
            }

            // Stage 2: Prose contamination check
            if (Regex.IsMatch(fixedCode, @"^(Here is|Sure|I've fixed|Below is|The corrected)", RegexOptions.IgnoreCase) && !fixedCode.StartsWith("using"))
            {
                return new ErrorFixResult { Success = false, ErrorMessage = "LLM output contained prose contamination" };
            }

            // Stage 3: Structure preservation
            var classNameMatch = Regex.Match(fileContent, @"class\s+(\w+)");
            if (classNameMatch.Success)
            {
                var className = classNameMatch.Groups[1].Value;
                if (!fixedCode.Contains(className))
                {
                    return new ErrorFixResult { Success = false, ErrorMessage = $"LLM output mangled the class structure: {className}" };
                }
            }

            // Stage 4: Diff size sanity
            double ratio = (double)fixedCode.Length / Math.Max(1, fileContent.Length);
            if (ratio < 0.3 || ratio > 2.0)
            {
                return new ErrorFixResult { Success = false, ErrorMessage = $"LLM output size ({fixedCode.Length} bytes) failed sanity check against original ({fileContent.Length} bytes)" };
            }

            // Stage 5: Backup before applying the fix
            var slnPath = await _vsEnvironment.GetWorkspaceRootAsync();
            var slnName = string.IsNullOrEmpty(slnPath) ? "UnknownSolution" : System.IO.Path.GetFileName(slnPath);
            await _backupService.BackupAsync(filePath, slnName);

            return new ErrorFixResult
            {
                Success = true,
                OriginalCode = fileContent,
                FixedCode = fixedCode
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fix failed for {FilePath}", filePath);
            return new ErrorFixResult
            {
                Success = false,
                ErrorMessage = $"Fix failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Parses a dotnet compilation error message to extract structured information.
    /// </summary>
    private static ParsedError ParseError(string errorMessage)
    {
        var result = new ParsedError { RawMessage = errorMessage };

        // Match pattern: File.cs(line,col): error CODE: message
        var match = Regex.Match(errorMessage, @"^(.+?)\((\d+),(\d+)\):\s*(error|warning)\s+(\w+):\s*(.+)$", RegexOptions.Multiline);
        if (match.Success)
        {
            result.FilePath = match.Groups[1].Value.Trim();
            result.LineNumber = int.Parse(match.Groups[2].Value);
            result.Column = int.Parse(match.Groups[3].Value);
            result.Severity = match.Groups[4].Value;
            result.ErrorCode = match.Groups[5].Value;
            result.Message = match.Groups[6].Value;
        }
        else
        {
            // Try simpler pattern: error CODE: message
            var simpleMatch = Regex.Match(errorMessage, @"(error|warning)\s+(\w+):\s*(.+)$", RegexOptions.Multiline);
            if (simpleMatch.Success)
            {
                result.Severity = simpleMatch.Groups[1].Value;
                result.ErrorCode = simpleMatch.Groups[2].Value;
                result.Message = simpleMatch.Groups[3].Value;
            }
        }

        return result;
    }

    private static string BuildFixPrompt(string filePath, string fileContent, string errorMessage, ParsedError parsedError)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Fix the following C# compilation error:");
        sb.AppendLine();
        sb.AppendLine($"**File:** {filePath}");

        if (parsedError.LineNumber > 0)
            sb.AppendLine($"**Line:** {parsedError.LineNumber}");

        if (!string.IsNullOrEmpty(parsedError.ErrorCode))
            sb.AppendLine($"**Error Code:** {parsedError.ErrorCode}");

        sb.AppendLine($"**Error:** {parsedError.Message ?? errorMessage}");
        sb.AppendLine();
        sb.AppendLine("**Current Code:**");
        sb.AppendLine("```csharp");
        sb.AppendLine(fileContent);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Provide the corrected code for the ENTIRE FILE. Output ONLY the complete fixed code, no explanations. Do not include markdown code fences.");

        return sb.ToString();
    }

    /// <summary>
    /// Removes markdown code fences and other non-code content from LLM response.
    /// </summary>
    private static string CleanCodeResponse(string response)
    {
        var text = response.Trim();

        // Remove markdown code fences
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline > 0)
            {
                text = text.Substring(firstNewline + 1);
            }
            if (text.EndsWith("```"))
            {
                text = text.Substring(0, text.Length - 3);
            }
            text = text.Trim();
        }

        return text;
    }

    private class ParsedError
    {
        public string RawMessage { get; set; } = "";
        public string? FilePath { get; set; }
        public int LineNumber { get; set; }
        public int Column { get; set; }
        public string? Severity { get; set; }
        public string? ErrorCode { get; set; }
        public string? Message { get; set; }
    }
}


