namespace AiAssistant.Engine.Services;

/// <summary>
/// Result of an error fix attempt.
/// </summary>
public record ErrorFixResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? OriginalCode { get; init; }
    public string? FixedCode { get; init; }
}

/// <summary>
/// Interface for fixing compilation errors using an LLM.
/// Implemented at the VSIX layer where the LLM client is available.
/// </summary>
public interface IErrorFixer
{
    /// <summary>
    /// Attempts to fix a compilation error using the LLM.
    /// </summary>
    /// <param name="filePath">Path to the file with the error.</param>
    /// <param name="fileContent">Current content of the file.</param>
    /// <param name="errorMessage">The compilation error message.</param>
    /// <param name="projectPath">Path to the project (for context).</param>
    /// <returns>An ErrorFixResult with the fixed code if successful.</returns>
    Task<ErrorFixResult> FixErrorAsync(
        string filePath,
        string fileContent,
        string errorMessage,
        string projectPath,
        CancellationToken ct = default);
}
