using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AiAssistant.Core.Services;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Task = System.Threading.Tasks.Task;

namespace ApexCode.Services;

public class VisualStudioEnvironmentService : IVisualStudioEnvironmentService, IDisposable
{
    private readonly Microsoft.VisualStudio.Shell.IAsyncServiceProvider _serviceProvider;
    private readonly IOutputLogger _outputLogger;
    private uint _solutionEventsCookie;
    private IVsSolution? _vsSolution;
    private SolutionEventSink? _eventSink;
    private System.IO.FileSystemWatcher? _folderWatcher;
    private ExplorerRefreshScheduler? _refreshScheduler;
    private bool _disposed;

    public event EventHandler? WorkspaceChanged;

    public VisualStudioEnvironmentService(Microsoft.VisualStudio.Shell.IAsyncServiceProvider serviceProvider, IOutputLogger outputLogger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _outputLogger = outputLogger ?? throw new ArgumentNullException(nameof(outputLogger));
        _ = InitializeEventsAsync();
    }

    private async Task InitializeEventsAsync()
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _vsSolution = await _serviceProvider.GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
            if (_vsSolution != null)
            {
                _eventSink = new SolutionEventSink(this);
                _vsSolution.AdviseSolutionEvents(_eventSink, out _solutionEventsCookie);
            }
            var root = await GetWorkspaceRootAsync();
            WatchFolder(root);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] Failed to initialize solution events: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _folderWatcher?.Dispose();
        _refreshScheduler?.Dispose();
        // UnadviseSolutionEvents must run on the UI thread (IVsSolution is a UI-thread COM object).
        // Dispose() can be called from any thread (DI teardown, using block on threadpool, etc.),
        // so marshal to the UI thread rather than calling ThrowIfNotOnUIThread() which would crash
        // the process during background teardown.
        if (_vsSolution == null || _solutionEventsCookie == 0)
            return;

        if (ThreadHelper.CheckAccess())
        {
            // Already on UI thread — unadvise directly.
#pragma warning disable VSTHRD010 // CheckAccess() above already verified we are on the UI thread
            _vsSolution.UnadviseSolutionEvents(_solutionEventsCookie);
#pragma warning restore VSTHRD010
            _solutionEventsCookie = 0;
        }
        else
        {
            // Switch to the UI thread for the COM call; fire-and-forget is acceptable here because
            // Dispose is a best-effort cleanup and VS is shutting down anyway.
            var solution = _vsSolution;
            var cookie = _solutionEventsCookie;
            _solutionEventsCookie = 0; // prevent double-unadvise
#pragma warning disable VSTHRD110 // Intentional fire-and-forget during disposal
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
#pragma warning disable VSTHRD010 // SwitchToMainThreadAsync above guarantees UI thread
                solution.UnadviseSolutionEvents(cookie);
#pragma warning restore VSTHRD010
            });
#pragma warning restore VSTHRD110
        }
    }

    private void NotifyWorkspaceChanged()
    {
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshWorkspaceWatcher()
    {
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try { WatchFolder(await GetWorkspaceRootAsync()); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ApexCode] Cannot resolve workspace watcher root: {ex.Message}"); }
        });
    }

    private void WatchFolder(string? path)
    {
        if (!_disposed && _folderWatcher != null && string.Equals(_folderWatcher.Path, path, StringComparison.OrdinalIgnoreCase)) return;
        _folderWatcher?.Dispose();
        _folderWatcher = null;
        _refreshScheduler?.Dispose();
        _refreshScheduler = null;
        if (_disposed || string.IsNullOrWhiteSpace(path) || !System.IO.Directory.Exists(path)) return;
        try
        {
            ExplorerRefreshScheduler? scheduler = null;
            scheduler = new ExplorerRefreshScheduler(ct => DispatchExplorerRefreshAsync(scheduler!, ct),
                ex => _outputLogger.Log($"[ApexCode] Explorer refresh failed: {ex.Message}"));
            _refreshScheduler = scheduler;
            _folderWatcher = new System.IO.FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.DirectoryName
            };
            _folderWatcher.Created += OnFolderEntryChanged;
            _folderWatcher.Deleted += OnFolderEntryChanged;
            _folderWatcher.Renamed += OnFolderEntryChanged;
            _folderWatcher.Error += (_, e) =>
            {
                _outputLogger.Log($"[ApexCode] Workspace watcher error: {e.GetException().Message}");
                if (ReferenceEquals(_, _folderWatcher)) _refreshScheduler?.Request();
            };
            _folderWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ApexCode] Cannot watch workspace: {ex.Message}"); }
    }

    private void OnFolderEntryChanged(object sender, System.IO.FileSystemEventArgs e)
    {
        if (!ReferenceEquals(sender, _folderWatcher)) return;
        var parts = (e.Name ?? "").Split('\\', '/');
        if (parts.Any(p => p.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                           p.Equals(".git_disabled", StringComparison.OrdinalIgnoreCase) ||
                           p.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                           p.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                           p.Equals("obj", StringComparison.OrdinalIgnoreCase))) return;
        _refreshScheduler?.Request();
    }

    private class SolutionEventSink : IVsSolutionEvents, IVsSolutionEvents7
    {
        private readonly VisualStudioEnvironmentService _parent;

        public SolutionEventSink(VisualStudioEnvironmentService parent)
        {
            _parent = parent;
        }

        public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution) { _parent.RefreshWorkspaceWatcher(); _parent.NotifyWorkspaceChanged(); return Microsoft.VisualStudio.VSConstants.S_OK; }
        public int OnAfterCloseSolution(object pUnkReserved) { _parent.WatchFolder(null); _parent.NotifyWorkspaceChanged(); return Microsoft.VisualStudio.VSConstants.S_OK; }
        public void OnAfterOpenFolder(string folderPath) { _parent.WatchFolder(folderPath); _parent.NotifyWorkspaceChanged(); }
        public void OnAfterCloseFolder(string folderPath) { _parent.WatchFolder(null); _parent.NotifyWorkspaceChanged(); }

        public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => Microsoft.VisualStudio.VSConstants.S_OK;
        public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => Microsoft.VisualStudio.VSConstants.S_OK;
        public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => Microsoft.VisualStudio.VSConstants.S_OK;
        public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => Microsoft.VisualStudio.VSConstants.S_OK;
        public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => Microsoft.VisualStudio.VSConstants.S_OK;
        public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => Microsoft.VisualStudio.VSConstants.S_OK;
        public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => Microsoft.VisualStudio.VSConstants.S_OK;
        public int OnBeforeCloseSolution(object pUnkReserved) => Microsoft.VisualStudio.VSConstants.S_OK;
        public void OnAfterLoadAllDeferredProjects() { }
        public void OnBeforeCloseFolder(string folderPath) { }
        public void OnQueryCloseFolder(string folderPath, ref int pfCancel) { }
    }

    private async Task<DTE2> GetDteAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await _serviceProvider.GetServiceAsync(typeof(DTE)) as DTE2;
        if (dte == null)
            throw new InvalidOperationException("Could not retrieve DTE2 service.");
        return dte;
    }

    public Task RefreshSolutionExplorerAsync()
    {
        _refreshScheduler?.Request();
        return Task.CompletedTask;
    }

    public IDisposable? DeferSolutionExplorerRefresh() => _refreshScheduler?.Defer();

    private async Task DispatchExplorerRefreshAsync(ExplorerRefreshScheduler scheduler, System.Threading.CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        ct.ThrowIfCancellationRequested();
        // Folder View is a different tool window/command route from Solution View.
        // Check both hosts (newer VS can embed Folder View in the standard frame),
        // including ViewHelper, where hosted panes expose their command handler.
        var shell = await _serviceProvider.GetServiceAsync(typeof(SVsUIShell)) as IVsUIShell;
        ct.ThrowIfCancellationRequested();
        if (!scheduler.ShouldDispatch()) return;
        if (shell == null) throw new InvalidOperationException("Visual Studio shell is unavailable.");
        var targets = new List<Microsoft.VisualStudio.OLE.Interop.IOleCommandTarget>();
        foreach (var id in new[] { SolutionExplorerRefreshCommand.FolderWindow, VSConstants.StandardToolWindows.SolutionExplorer })
        {
            var windowId = id;
            if (ErrorHandler.Failed(shell.FindToolWindow(0, ref windowId, out var frame)) || frame == null) continue;
            foreach (var property in new[] { __VSFPROPID.VSFPROPID_DocView, __VSFPROPID.VSFPROPID_ViewHelper })
            {
                if (ErrorHandler.Succeeded(frame.GetProperty((int)property, out var view)) &&
                    view is Microsoft.VisualStudio.OLE.Interop.IOleCommandTarget target && !targets.Contains(target))
                    targets.Add(target);
            }
        }
        foreach (var folderView in new[] { true, false })
            foreach (var target in targets)
                if (SolutionExplorerRefreshCommand.TryExecute(target, folderView))
                {
                    _outputLogger.Log($"[ApexCode] Explorer refresh dispatched: {(folderView ? "Folder View" : "Solution View")}");
                    return;
                }
        throw new InvalidOperationException("No explorer host accepted the refresh command. Open Folder View and retry.");
    }

    public async Task<string?> GetWorkspaceRootAsync(System.Threading.CancellationToken ct = default)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        // Open Folder does not necessarily populate IVsSolution or DTE.Solution.
        var folderService = await _serviceProvider.GetServiceAsync(typeof(Microsoft.VisualStudio.Workspace.VSIntegration.Contracts.IVsFolderWorkspaceService))
            as Microsoft.VisualStudio.Workspace.VSIntegration.Contracts.IVsFolderWorkspaceService;
        var folderLocation = folderService?.CurrentWorkspace?.Location;
        if (!string.IsNullOrWhiteSpace(folderLocation))
        {
            LogResolverBranch("folder_workspace");
            return NormalizeRoot(folderLocation);
        }

        // 1. Try IVsSolution first (Authoritative, non-I/O source for .sln and folder mode in modern VS)
        if (await _serviceProvider.GetServiceAsync(typeof(SVsSolution)) is IVsSolution vsSolution)
        {
            try
            {
                if (vsSolution.GetSolutionInfo(out string solDir, out _, out _) == 0
                    && !string.IsNullOrWhiteSpace(solDir))
                {
                    LogResolverBranch("ivs_solution");
                    return NormalizeRoot(solDir);
                }
            }
            catch { /* fall through to DTE */ }
        }

        // 2. Fallback to DTE
        var dte = await GetDteAsync();
        var solution = dte.Solution;
        if (solution is { IsOpen: true })
        {
            var solutionPath = solution.FullName;
            if (!string.IsNullOrEmpty(solutionPath))
            {
                // FIX: In "Open Folder" mode, FullName IS the directory.
                // Existence check is disk I/O — run it off the UI thread to prevent shell hangs.
                bool isDirectory = await Task.Run(() => System.IO.Directory.Exists(solutionPath), ct);

                if (isDirectory)
                {
                    LogResolverBranch("dte_folder");
                    return NormalizeRoot(solutionPath);
                }

                var dir = System.IO.Path.GetDirectoryName(solutionPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    LogResolverBranch("dte_solution");
                    return NormalizeRoot(dir);
                }
            }
        }

        // 3. Terminal fallback
        LogResolverBranch("null");
        return null;
    }

    private void LogResolverBranch(string branch)
    {
        try
        {
            _outputLogger?.Log($"[ApexCode] workspace_root_resolved branch={branch}");
        }
        catch
        {
            // Telemetry/logging must never fail the resolution path.
        }
    }

    private static string NormalizeRoot(string path)
    {
        // Canonicalizes separators/relative segments; trims trailing separators while preserving drive roots ("C:\").
        var full = System.IO.Path.GetFullPath(path.Trim());
        if (full.Length > 3)
            full = full.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        return full;
    }

    public async Task<IReadOnlyList<string>> GetOpenDocumentPathsAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await GetDteAsync();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Document document in dte.Documents)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(document.FullName))
                    paths.Add(document.FullName);
            }
            catch (Exception)
            {
                // A document can disappear while the COM collection is being enumerated.
            }
        }

        return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<string?> GetActiveDocumentAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dte = await GetDteAsync();
            var activeDocument = dte.ActiveDocument;
            if (activeDocument != null && !string.IsNullOrWhiteSpace(activeDocument.FullName))
            {
                return activeDocument.FullName;
            }
        }
        catch
        {
            // Ignore COM exceptions or null refs if no document is active
        }
        return null;
    }

    public async Task<IEnumerable<string>> GetLoadedProjectsAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var folderWorkspaceService = await _serviceProvider.GetServiceAsync(typeof(Microsoft.VisualStudio.Workspace.VSIntegration.Contracts.IVsFolderWorkspaceService))
                                          as Microsoft.VisualStudio.Workspace.VSIntegration.Contracts.IVsFolderWorkspaceService;
        var currentWorkspace = folderWorkspaceService?.CurrentWorkspace;

        if (currentWorkspace != null && !string.IsNullOrEmpty(currentWorkspace.Location))
        {
            // FIX Issue #3: Offload disk I/O to background thread to prevent UI freezes
            return await Task.Run(() => GetProjectsFromOpenFolder(currentWorkspace.Location)).ConfigureAwait(true);
        }

        return await GetProjectsFromDteSolutionAsync();
    }

    private static readonly HashSet<string> ExcludedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".vscode", ".idea",
        "bin", "obj", "node_modules", "dist", "build", "out",
        "packages", ".nuget", "__pycache__", ".venv", "venv"
    };

    private List<string> GetProjectsFromOpenFolder(string workspaceRoot)
    {
        // FIX Issue #3: This method is called from GetLoadedProjectsAsync which is on the UI thread.
        // Move disk I/O to background to prevent UI freezes on large workspaces.
        var projects = new List<string>();

        // Offload disk enumeration to background thread
        try
        {
            var topLevelDirs = System.IO.Directory.EnumerateDirectories(workspaceRoot).ToList();
            var hasLooseFiles = SafeEnumerateFiles(workspaceRoot).Any();

            foreach (var dir in topLevelDirs)
            {
                var dirInfo = new System.IO.DirectoryInfo(dir);
                if (dirInfo.Attributes.HasFlag(System.IO.FileAttributes.Hidden)) continue;

                var folderName = dirInfo.Name;
                if (string.IsNullOrEmpty(folderName) || ExcludedFolderNames.Contains(folderName))
                {
                    continue;
                }

                projects.Add(dir);
            }

            if (hasLooseFiles)
            {
                projects.Add(workspaceRoot);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException)
        {
            System.Diagnostics.Debug.WriteLine($"[GetProjectsFromOpenFolder] Failed to enumerate '{workspaceRoot}': {ex.Message}");
        }

        return projects;
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dir)
    {
        try { return System.IO.Directory.EnumerateFiles(dir); }
        catch { return Enumerable.Empty<string>(); }
    }

    private async Task<List<string>> GetProjectsFromDteSolutionAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await GetDteAsync();
        var projects = new List<string>();

        if (dte.Solution == null || !dte.Solution.IsOpen)
            return projects;

        foreach (Project project in dte.Solution.Projects)
        {
            AddProjectPaths(project, projects);
        }

        return projects;
    }

    private void AddProjectPaths(Project project, List<string> projects)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (project.Kind == ProjectKinds.vsProjectKindSolutionFolder)
            {
                foreach (ProjectItem item in project.ProjectItems)
                {
                    if (item.SubProject != null)
                    {
                        AddProjectPaths(item.SubProject, projects);
                    }
                }
            }
            else if (!string.IsNullOrEmpty(project.FullName))
            {
                projects.Add(project.FullName);
            }
        }
        catch
        {
            // Ignore unreadable projects
        }
    }

    public async Task<string> ExecuteBuildAsync(string target = "Build")
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dte = await GetDteAsync();
            if (dte.Solution == null || !dte.Solution.IsOpen)
                return "❌ No solution is currently open.";

            var tcs = new TaskCompletionSource<string>();
            EnvDTE._dispBuildEvents_OnBuildDoneEventHandler onBuildDone = null!;
            onBuildDone = (EnvDTE.vsBuildScope scope, EnvDTE.vsBuildAction action) =>
            {
                DetachBuildEvent(dte, onBuildDone);
                tcs.TrySetResult($"✅ {target} command completed via Visual Studio.");
            };
            
            AttachBuildEvent(dte, onBuildDone);

            if (target.Equals("clean", StringComparison.OrdinalIgnoreCase))
            {
                dte.Solution.SolutionBuild.Clean(false);
            }
            else
            {
                dte.Solution.SolutionBuild.Build(false);
            }

            return await tcs.Task;
        }
        catch (Exception ex)
        {
            return $"❌ Visual Studio build failed: {ex.Message}";
        }
    }

    public async Task<IEnumerable<DiagnosticEntry>> GetErrorListDiagnosticsAsync(string severity = "all")
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var diagnostics = new List<DiagnosticEntry>();

        try
        {
            var errorList = await _serviceProvider.GetServiceAsync(typeof(SVsErrorList)) as IVsErrorList;
            if (errorList == null)
            {
                System.Diagnostics.Debug.WriteLine("Could not retrieve IVsErrorList service.");
                return diagnostics;
            }

            var dte = await GetDteAsync();
            var errorListWindow = dte.ToolWindows.ErrorList;

            if (errorListWindow != null && errorListWindow.ErrorItems != null)
            {
                var filterSeverity = severity.ToLowerInvariant();
                for (int i = 1; i <= errorListWindow.ErrorItems.Count; i++)
                {
                    try
                    {
                        var item = errorListWindow.ErrorItems.Item(i);
                        
                        string mappedSeverity = "Message";
                        if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh)
                            mappedSeverity = "Error";
                        else if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelMedium)
                            mappedSeverity = "Warning";

                        if (filterSeverity == "error" && mappedSeverity != "Error")
                            continue;
                        if (filterSeverity == "warning" && mappedSeverity != "Warning")
                            continue;

                        diagnostics.Add(new DiagnosticEntry(
                            mappedSeverity,
                            item.FileName ?? "",
                            item.Line,
                            item.Column,
                            item.Description ?? ""
                        ));
                    }
                    catch
                    {
                        // Ignore malformed items that throw COM exceptions when accessing properties
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to read Visual Studio error list: {ex.Message}");
        }

        return diagnostics;
    }

    private void AttachBuildEvent(DTE2 dte, _dispBuildEvents_OnBuildDoneEventHandler handler)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        dte.Events.BuildEvents.OnBuildDone += handler;
    }

    private void DetachBuildEvent(DTE2 dte, _dispBuildEvents_OnBuildDoneEventHandler handler)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        dte.Events.BuildEvents.OnBuildDone -= handler;
    }

    public async Task<Microsoft.CodeAnalysis.Workspace?> GetWorkspaceAsync()
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var componentModel = await _serviceProvider.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
            return componentModel?.GetService<VisualStudioWorkspace>();
        }
        catch
        {
            return null;
        }
    }
    public async Task OpenDiffViewerAsync(string leftFilePath, string rightFilePath, string title)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dte = await GetDteAsync();
            
            // Visual Studio diff command: Tools.DiffFiles "File1" "File2" "Title"
            // The title argument is optional but useful. We must properly quote the paths.
            string args = $"\"{leftFilePath}\" \"{rightFilePath}\"";
            if (!string.IsNullOrEmpty(title))
            {
                args += $" \"{title}\"";
            }
            dte.ExecuteCommand("Tools.DiffFiles", args);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open diff viewer for {leftFilePath} and {rightFilePath}: {ex}");
        }
    }

    public async Task OpenFileAsync(string filePath)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dte = await GetDteAsync();
            dte.ItemOperations.OpenFile(filePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open file {filePath}: {ex}");
            throw;
        }
    }
}

