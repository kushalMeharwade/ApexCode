using System.Collections.Generic;
using System.Threading.Tasks;

namespace AiAssistant.Core.Services;

/// <summary>
/// Provides access to Visual Studio Environment APIs (DTE, Error List) for AI tools.
/// Abstracted in Core so tools don't need direct VSSDK references.
/// </summary>
public interface IVisualStudioEnvironmentService
{
    /// <summary>Queues a coalesced refresh after file activity settles; does not await VS tree reconstruction.</summary>
    Task RefreshSolutionExplorerAsync();
    /// <summary>Defers automatic Explorer refresh while a file operation is in progress.</summary>
    System.IDisposable? DeferSolutionExplorerRefresh();
    /// <summary>
    /// Gets the full path to the currently active workspace root (solution directory or opened folder).
    /// </summary>
    Task<string?> GetWorkspaceRootAsync(System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Gets the full paths of all currently loaded projects in the solution.
    /// </summary>
    Task<IEnumerable<string>> GetLoadedProjectsAsync();

    /// <summary>
    /// Gets full paths for documents currently open in Visual Studio.
    /// Unsaved documents without a path are omitted.
    /// </summary>
    Task<IReadOnlyList<string>> GetOpenDocumentPathsAsync();

    /// <summary>
    /// Gets the full path to the currently active (focused) document in Visual Studio.
    /// </summary>
    Task<string?> GetActiveDocumentAsync();

    /// <summary>
    /// Executes a build command on the active solution (e.g. Build, Rebuild, Clean).
    /// </summary>
    Task<string> ExecuteBuildAsync(string target = "Build");

    /// <summary>
    /// Gets a list of current diagnostics (errors, warnings) from the Visual Studio Error List.
    /// </summary>
    Task<IEnumerable<DiagnosticEntry>> GetErrorListDiagnosticsAsync(string severity = "all");

    /// <summary>
    /// Gets the active Roslyn Workspace.
    /// </summary>
    Task<Microsoft.CodeAnalysis.Workspace?> GetWorkspaceAsync();

    /// <summary>
    /// Fired when the active workspace (solution or folder) changes.
    /// </summary>
    event System.EventHandler? WorkspaceChanged;

    /// <summary>
    /// Opens the Visual Studio native diff viewer to compare two files.
    /// </summary>
    Task OpenDiffViewerAsync(string leftFilePath, string rightFilePath, string title);

    /// <summary>
    /// Opens a file in the Visual Studio editor.
    /// </summary>
    Task OpenFileAsync(string filePath);
}

public record DiagnosticEntry(string Severity, string FileName, int Line, int Column, string Description);
