namespace AiAssistant.Engine.Services;

public interface ICompileValidator
{
    Task<bool> ValidateAsync(string projectPath, CancellationToken ct = default);
    Task<CompilationResult> CompileAsync(string projectPath, CancellationToken ct = default);
}

public record CompilationResult
{
    public bool Success { get; init; }
    public IEnumerable<string> Errors { get; init; } = Enumerable.Empty<string>();
    public IEnumerable<string> Warnings { get; init; } = Enumerable.Empty<string>();
}
