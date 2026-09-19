using System.Diagnostics;

namespace AiAssistant.Engine.Services;

public class CompileValidator : ICompileValidator
{
    // Default timeout: 5 minutes for dotnet build
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public async Task<bool> ValidateAsync(string projectPath, CancellationToken ct = default)
    {
        var result = await CompileAsync(projectPath, ct);
        return result.Success;
    }

    public async Task<CompilationResult> CompileAsync(string projectPath, CancellationToken ct = default)
    {
        try
        {
            var projectFile = projectPath;
            if (Directory.Exists(projectPath))
            {
                projectFile = Directory.GetFiles(projectPath, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (string.IsNullOrEmpty(projectFile))
                {
                    return new CompilationResult { Success = false, Errors = new[] { "No .csproj found in directory." } };
                }
            }

            using var workspace = Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace.Create();
            var project = await workspace.OpenProjectAsync(projectFile, cancellationToken: ct);
            var compilation = await project.GetCompilationAsync(ct);
            
            if (compilation == null)
            {
                return new CompilationResult { Success = false, Errors = new[] { "Failed to get Roslyn compilation for project." } };
            }

            var diagnostics = compilation.GetDiagnostics(cancellationToken: ct);
            var errors = diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
            var warnings = diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Warning).ToList();

            return new CompilationResult
            {
                Success = errors.Count == 0,
                Errors = errors.Select(d => $"{d.Location.GetLineSpan().Path}({d.Location.GetLineSpan().StartLinePosition.Line + 1}): {d.GetMessage()}"),
                Warnings = warnings.Select(d => d.GetMessage())
            };
        }
        catch (Exception ex)
        {
            return new CompilationResult
            {
                Success = false,
                Errors = new[] { $"Compilation exception: {ex.Message}" }
            };
        }
    }


}
