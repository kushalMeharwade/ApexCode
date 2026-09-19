using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using AiAssistant.Core.Models;
using AiAssistant.Core.Services;
using AiAssistant.Engine.Models;
using AiAssistant.Engine.Services;

namespace ApexCode.Services
{
    [Export(typeof(IEditApplicationService))]
    public class VsEditApplicationService : IEditApplicationService
    {
        private readonly SVsServiceProvider _serviceProvider;
        private readonly ITextDocumentFactoryService _textDocumentFactoryService;
        private readonly FuzzyMatchFinder _matchFinder;

        private readonly ITextUndoHistoryRegistry _undoHistoryRegistry;
        private readonly IVsInvisibleEditorManager _invisibleEditorManager;

        [ImportingConstructor]
        public VsEditApplicationService(
            [Import(typeof(SVsServiceProvider))] IServiceProvider serviceProvider,
            ITextDocumentFactoryService textDocumentFactoryService,
            FuzzyMatchFinder matchFinder,
            [Import(AllowDefault = true)] ITextUndoHistoryRegistry undoHistoryRegistry)
        {
            _serviceProvider = (SVsServiceProvider)serviceProvider;
            _textDocumentFactoryService = textDocumentFactoryService;
            _matchFinder = matchFinder;
            _undoHistoryRegistry = undoHistoryRegistry;
            
            ThreadHelper.ThrowIfNotOnUIThread();
            _invisibleEditorManager = (IVsInvisibleEditorManager)_serviceProvider.GetService(typeof(SVsInvisibleEditorManager));
        }

        public async Task<AiAssistant.Core.Models.EditResult> ApplyEditsAsync(EditIntent intent, object preEditStateObj, CancellationToken ct = default)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var preEditState = preEditStateObj as EditSessionState;
            var result = new AiAssistant.Core.Models.EditResult { Status = EditResultStatus.Success };

            if (intent.Edits == null || intent.Edits.Count == 0)
                return result;

            var validatedEdits = new List<(EditOperation edit, ITextBuffer buffer, MatchResult match)>();
            var openedEditors = new List<IVsInvisibleEditor>();

            try
            {
                // Validate and Resolve Buffers
                foreach (var edit in intent.Edits)
                {
                    ITextBuffer targetBuffer = ResolveBuffer(edit.FilePath, openedEditors);
                    if (targetBuffer == null)
                    {
                        result.FailedEdits.Add(new FailedEdit(edit.FilePath, edit.Search, "Could not resolve text buffer for file.", null));
                        continue;
                    }

                    // Task 3: Document Version Guard check
                    if (preEditState != null && string.Equals(edit.FilePath, preEditState.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        var currentVersion = targetBuffer.CurrentSnapshot.Version.VersionNumber;
                        if (currentVersion != preEditState.VersionNumber)
                        {
                            // A real implementation would compute overlap. We'll simply block it if changes were made.
                            result.Status = EditResultStatus.VersionConflict;
                            result.FailedEdits.Add(new FailedEdit(edit.FilePath, edit.Search, "Version conflict: The document was modified during AI generation.", null));
                            return result;
                        }
                    }

                    string sourceText = targetBuffer.CurrentSnapshot.GetText();
                    var match = _matchFinder.FindMatch(sourceText, edit.Search);
                    System.Diagnostics.Debug.WriteLine($"[PIPELINE] 6. Match Result - File: {edit.FilePath}, Type: {match.Type}, Score: {match.ConfidenceScore}");

                    if (match.Type == MatchType.None || match.ConfidenceScore < 0.8)
                    {
                        result.FailedEdits.Add(new FailedEdit(edit.FilePath, edit.Search, "Search text not found or confidence too low.", match.ConfidenceScore));
                    }
                    else
                    {
                        validatedEdits.Add((edit, targetBuffer, match));
                    }
                }

                if (result.FailedEdits.Count > 0)
                {
                    result.Status = EditResultStatus.PartialSuccess;
                    return result;
                }

                // Apply edits within transactions
                var groupedEdits = validatedEdits
                    .GroupBy(x => x.buffer)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.match.Position).ToList());

                foreach (var group in groupedEdits)
                {
                    var buffer = group.Key;
                    
                    ITextUndoTransaction singleUndo = null;
                    if (_undoHistoryRegistry != null)
                    {
                        var history = _undoHistoryRegistry.GetHistory(buffer);
                        singleUndo = history?.CreateTransaction("AI Edit");
                    }

                    try
                    {
                        using (var textEdit = buffer.CreateEdit())
                        {
                            foreach (var item in group.Value)
                            {
                                var span = new Span(item.match.Position, item.match.Length);
                                textEdit.Replace(span, item.edit.Replace);

                                result.AppliedEdits.Add(new AppliedEdit(
                                    item.edit.FilePath,
                                    item.match.Position,
                                    item.match.Length,
                                    item.edit.Search,
                                    item.edit.Replace
                                ));
                            }
                            textEdit.Apply();
                        }
                        singleUndo?.Complete();
                    }
                    catch (Exception)
                    {
                        singleUndo?.Cancel();
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                result.Status = EditResultStatus.RetryableError;
                result.FailedEdits.Add(new FailedEdit("Unknown", "N/A", $"Error applying edits: {ex.Message}", null));
                result.AppliedEdits.Clear();
            }
            finally
            {
                // Release invisible editors
                foreach (var editor in openedEditors)
                {
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(editor);
                }
            }

            return result;
        }

        private ITextBuffer ResolveBuffer(string filePath, List<IVsInvisibleEditor> openedEditors)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            
            // Check if open in an active view first
            // Note: In real life, we would enumerate all active documents. 
            // For simplicity, we use the invisible editor manager which will either return the active buffer or load it.
            if (_invisibleEditorManager != null)
            {
                var hr = _invisibleEditorManager.RegisterInvisibleEditor(
                    filePath, 
                    null, 
                    0, // Just some default flag
                    null, 
                    out var invisibleEditor);

                if (hr == Microsoft.VisualStudio.VSConstants.S_OK && invisibleEditor != null)
                {
                    openedEditors.Add(invisibleEditor);
                    
                    IntPtr docDataPtr = IntPtr.Zero;
                    hr = invisibleEditor.GetDocData(0, typeof(IVsTextLines).GUID, out docDataPtr);
                    if (hr == Microsoft.VisualStudio.VSConstants.S_OK && docDataPtr != IntPtr.Zero)
                    {
                        var docData = System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(docDataPtr);
                        System.Runtime.InteropServices.Marshal.Release(docDataPtr);

                        var componentModel = (IComponentModel)_serviceProvider.GetService(typeof(SComponentModel));
                        var adaptersFactory = componentModel.GetService<IVsEditorAdaptersFactoryService>();
                        var buffer = adaptersFactory.GetDocumentBuffer(docData as IVsTextBuffer);
                        if (buffer != null)
                        {
                            return buffer;
                        }
                    }
                }
            }

            return null;
        }
    }
}
