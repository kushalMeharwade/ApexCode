using AiAssistant.Engine.Models;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Engine.Services;

public class AutoHealingLoop
{
    private readonly IRoslynEditService _editService;
    private readonly ICompileValidator _validator;
    private readonly IErrorFixer _errorFixer;
    private readonly ILogger<AutoHealingLoop> _logger;
    private readonly int _maxIterations;

    public AutoHealingLoop(
        IRoslynEditService editService,
        ICompileValidator validator,
        IErrorFixer errorFixer,
        ILogger<AutoHealingLoop> logger,
        int maxIterations = 2)
    {
        _editService = editService ?? throw new ArgumentNullException(nameof(editService));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _errorFixer = errorFixer ?? throw new ArgumentNullException(nameof(errorFixer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxIterations = maxIterations;
    }

    public async Task<AutoHealingResult> RunAsync(string projectPath, string filePath, string compilationError, CancellationToken ct = default)
    {
        var currentError = compilationError;

        for (int i = 0; i < _maxIterations; i++)
        {
            _logger.LogInformation("Auto-healing iteration {Iteration}/{MaxIterations} for {FilePath}",
                i + 1, _maxIterations, filePath);

            ct.ThrowIfCancellationRequested();

            // 1. Ask the LLM to fix the error
            var fixResult = await FixCompilationErrorAsync(projectPath, filePath, currentError, ct);

            if (!fixResult.Success)
            {
                _logger.LogWarning("Auto-healing fix attempt {Iteration} failed: {Error}",
                    i + 1, fixResult.ErrorMessage);
                break;
            }

            // 2. Validate the fix by running a build
            var compileResult = await _validator.CompileAsync(projectPath, ct);

            if (compileResult.Success)
            {
                _logger.LogInformation("Auto-healing succeeded after {Iteration} iteration(s)", i + 1);
                return new AutoHealingResult
                {
                    Success = true,
                    Iterations = i + 1,
                    FixedCode = fixResult.FixedCode
                };
            }

            // 3. Build still fails — extract the new error and try again
            currentError = string.Join("; ", compileResult.Errors);
            _logger.LogDebug("Build still failing after fix. New error: {Error}", currentError);
        }

        _logger.LogWarning("Auto-healing failed after {MaxIterations} iterations", _maxIterations);
        return new AutoHealingResult
        {
            Success = false,
            Iterations = _maxIterations,
            ErrorMessage = currentError
        };
    }

    private async Task<ErrorFixResult> FixCompilationErrorAsync(string projectPath, string filePath, string error, CancellationToken ct)
    {
        try
        {
            // Read the current file content
            if (!File.Exists(filePath))
            {
                return new ErrorFixResult
                {
                    Success = false,
                    ErrorMessage = $"File not found: {filePath}"
                };
            }

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > 10 * 1024 * 1024) // 10 MB limit
            {
                return new ErrorFixResult
                {
                    Success = false,
                    ErrorMessage = $"File too large to auto-fix: {filePath}"
                };
            }

            var fileContent = await _editService.GetFileContentAsync(filePath, ct);

            // Ask the LLM to fix the error
            var fixResult = await _errorFixer.FixErrorAsync(filePath, fileContent, error, projectPath, ct);

            if (!fixResult.Success || string.IsNullOrWhiteSpace(fixResult.FixedCode))
            {
                return fixResult;
            }

            var expectedSha = AiAssistant.Engine.SkeletonEngine.ContentHash.Compute(fileContent);
            var editResult = await _editService.ReplaceEntireFileAsync(filePath, expectedSha, fixResult.FixedCode, ct);
            if (!editResult.Success)
            {
                return new ErrorFixResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to apply fix: {editResult.ErrorMessage}"
                };
            }

            return new ErrorFixResult
            {
                Success = true,
                OriginalCode = fileContent,
                FixedCode = fixResult.FixedCode
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-healing fix failed for {FilePath}", filePath);
            return new ErrorFixResult
            {
                Success = false,
                ErrorMessage = $"Fix exception: {ex.Message}"
            };
        }
    }
}

public record AutoHealingResult
{
    public bool Success { get; init; }
    public int Iterations { get; init; }
    public string? FixedCode { get; init; }
    public string? ErrorMessage { get; init; }
}
