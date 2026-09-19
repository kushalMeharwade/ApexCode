using AiAssistant.Engine.Models;
using System.Threading;

namespace AiAssistant.Engine.Services;

public interface IRoslynEditService
{
    /// <summary>
    /// Replaces the first occurrence of <paramref name="oldText"/> in the file.
    /// When <paramref name="expectedOccurrences"/> is provided the method fails if the
    /// actual occurrence count does not match, preventing silent multi-site corruption.
    /// </summary>
    Task<EditResult> ApplyEditAsync(string filePath, string oldText, string newText,
        int? expectedOccurrences = null, CancellationToken ct = default);

    Task<EditResult> ReplaceTextAsync(string filePath, int startLine, int startColumn,
        int endLine, int endColumn, string newText, CancellationToken ct = default);

    Task<string> GetFileContentAsync(string filePath, CancellationToken ct = default);
    Task<IEnumerable<string>> FindUsagesAsync(string filePath, int line, int column, CancellationToken ct = default);
    Task<IEnumerable<string>> GetCompilationErrorsAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Replaces the entire file content, validating it as a valid C# CompilationUnit
    /// if the file is a C# file. Requires optimistic concurrency via expectedSha.
    /// </summary>
    Task<EditResult> ReplaceEntireFileAsync(string filePath, string expectedSha, string newContent, CancellationToken ct = default);

    /// <summary>
    /// Applies the edit using Roslyn AST manipulation for C# files; falls back to
    /// <see cref="ApplyEditAsync"/> for other file types.
    /// When <paramref name="expectedOccurrences"/> is provided the same occurrence guard applies.
    /// </summary>
    Task<EditResult> ApplyRoslynEditAsync(string filePath, string oldText, string newText,
        int? expectedOccurrences = null, CancellationToken ct = default);
}
