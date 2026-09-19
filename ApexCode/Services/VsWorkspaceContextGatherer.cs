using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using AiAssistant.Core.Models;
using Task = System.Threading.Tasks.Task;

namespace ApexCode.Services
{
    [Export]
    public class VsWorkspaceContextGatherer
    {
        private readonly SVsServiceProvider _serviceProvider;
        private readonly IVsEditorAdaptersFactoryService _editorAdaptersFactoryService;
        private readonly VisualStudioWorkspace _workspace;
        private readonly ITextDocumentFactoryService _textDocumentFactoryService;

        [ImportingConstructor]
        public VsWorkspaceContextGatherer(
            [Import(typeof(SVsServiceProvider))] IServiceProvider serviceProvider,
            IVsEditorAdaptersFactoryService editorAdaptersFactoryService,
            VisualStudioWorkspace workspace,
            ITextDocumentFactoryService textDocumentFactoryService)
        {
            _serviceProvider = (SVsServiceProvider)serviceProvider;
            _editorAdaptersFactoryService = editorAdaptersFactoryService;
            _workspace = workspace;
            _textDocumentFactoryService = textDocumentFactoryService;
        }

        public async Task<EditorContextData> ExtractContextAsync(CancellationToken ct)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var context = new EditorContextData();

            // Step 1: Get Active Text View
            var textManager = (IVsTextManager)_serviceProvider.GetService(typeof(SVsTextManager));
            if (textManager == null) throw new InvalidOperationException("TextManager not available.");

            textManager.GetActiveView(1, null, out var activeView);
            if (activeView == null) throw new InvalidOperationException("No active code editor.");

            var wpfTextView = _editorAdaptersFactoryService.GetWpfTextView(activeView);
            if (wpfTextView == null) throw new InvalidOperationException("Could not get WPF text view.");

            // Step 2: Extract Editor State
            var buffer = wpfTextView.TextBuffer;
            var snapshot = buffer.CurrentSnapshot;
            var caretPosition = wpfTextView.Caret.Position.BufferPosition.Position;
            
            var selectedSpan = wpfTextView.Selection.SelectedSpans.FirstOrDefault();
            string selectedText = selectedSpan != default ? selectedSpan.GetText() : string.Empty;

            _textDocumentFactoryService.TryGetTextDocument(buffer, out var textDocument);
            string filePath = textDocument?.FilePath ?? string.Empty;

            var line = snapshot.GetLineFromPosition(caretPosition);
            int cursorLine = line.LineNumber + 1; // 1-based

            context.FilePath = filePath;
            context.FileContent = snapshot.GetText();
            context.SelectedText = selectedText;
            context.CursorPosition = caretPosition;
            context.CursorLine = cursorLine;
            context.VersionNumber = snapshot.Version.VersionNumber;

            // Step 3: Get Roslyn Document
            if (!string.IsNullOrEmpty(filePath) && filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                var documentId = _workspace.CurrentSolution.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();
                if (documentId != null)
                {
                    var document = _workspace.CurrentSolution.GetDocument(documentId);
                    if (document != null)
                    {
                        var project = document.Project;
                        context.TargetFramework = project.Name;
                        
                        // Step 4: Get Symbol under cursor
                        var semanticModel = await document.GetSemanticModelAsync(ct);
                        var root = await document.GetSyntaxRootAsync(ct);
                        
                        if (semanticModel != null && root != null)
                        {
                            var token = root.FindToken(caretPosition);
                            var symbol = semanticModel.GetSymbolInfo(token.Parent).Symbol ?? semanticModel.GetDeclaredSymbol(token.Parent);
                            if (symbol == null && token.Parent?.Parent != null)
                            {
                                symbol = semanticModel.GetSymbolInfo(token.Parent.Parent).Symbol ?? semanticModel.GetDeclaredSymbol(token.Parent.Parent);
                            }

                            if (symbol != null)
                            {
                                context.SymbolName = symbol.Name;
                                var defLoc = symbol.Locations.FirstOrDefault();
                                if (defLoc != null && defLoc.IsInSource)
                                {
                                    var defSyntax = defLoc.SourceTree?.GetRoot().FindNode(defLoc.SourceSpan);
                                    context.SymbolDefinition = defSyntax?.ToString() ?? "";
                                }
                                context.ContainingType = symbol.ContainingType?.Name ?? "";
                            }
                            
                            // Step 6: Get Diagnostics
                            var diagnostics = semanticModel.GetDiagnostics(cancellationToken: ct)
                                .Where(d => d.Severity == DiagnosticSeverity.Error)
                                .Take(10);
                            
                            foreach(var d in diagnostics)
                            {
                                context.ActiveDiagnostics.Add($"{d.Id}: {d.GetMessage()} (Line {d.Location.GetLineSpan().StartLinePosition.Line + 1})");
                            }
                        }

                        // Step 5: Get Package References
                        if (!string.IsNullOrEmpty(project.FilePath))
                        {
                            try 
                            {
                                var msbuildProj = Microsoft.Build.Evaluation.ProjectCollection.GlobalProjectCollection.LoadedProjects
                                    .FirstOrDefault(p => string.Equals(p.FullPath, project.FilePath, StringComparison.OrdinalIgnoreCase))
                                    ?? new Microsoft.Build.Evaluation.Project(project.FilePath);

                                var packages = msbuildProj.GetItems("PackageReference")
                                    .Select(item => item.EvaluatedInclude)
                                    .OrderBy(x => x)
                                    .Take(30)
                                    .ToList();

                                context.NuGetPackages.AddRange(packages);
                            }
                            catch
                            {
                                // Return empty package list on MSBuild failure
                            }
                        }
                    }
                }
            }

            return context;
        }
    }
}
