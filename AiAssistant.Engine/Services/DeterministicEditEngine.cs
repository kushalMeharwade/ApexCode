using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading.Tasks;
using AiAssistant.Core.Models;
using AiAssistant.Engine.Models;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;

namespace AiAssistant.Engine.Services
{
    [Export]
    public class DeterministicEditEngine
    {
        private readonly FuzzyMatchFinder _matchFinder;
        private readonly ITextUndoHistoryRegistry _undoHistoryRegistry;

        [ImportingConstructor]
        public DeterministicEditEngine(
            FuzzyMatchFinder matchFinder,
            [Import(AllowDefault = true)] ITextUndoHistoryRegistry undoHistoryRegistry)
        {
            _matchFinder = matchFinder;
            _undoHistoryRegistry = undoHistoryRegistry;
        }

        public EditSessionState CaptureState(ITextBuffer buffer, string filePath, IReadOnlyList<Diagnostic> diagnostics = null)
        {
            return new EditSessionState(buffer.CurrentSnapshot.Version.VersionNumber, filePath, diagnostics ?? Array.Empty<Diagnostic>());
        }

        public async Task<AiAssistant.Core.Models.EditResult> ApplyEditsAsync(
            EditIntent intent, 
            Func<string, ITextBuffer> bufferResolver, 
            EditSessionState preEditState)
        {
            await Task.CompletedTask;
            var result = new AiAssistant.Core.Models.EditResult { Status = EditResultStatus.Success };

            if (intent.Edits == null || intent.Edits.Count == 0)
            {
                result.Status = EditResultStatus.Success;
                return result;
            }

            // Phase 1: Validation
            var validatedEdits = new List<(EditOperation edit, ITextBuffer buffer, MatchResult match)>();

            foreach (var edit in intent.Edits)
            {
                ITextBuffer targetBuffer;
                try
                {
                    targetBuffer = bufferResolver(edit.FilePath ?? "");
                }
                catch (Exception ex)
                {
                    result.FailedEdits.Add(new FailedEdit(edit.FilePath ?? "", edit.Search ?? "", $"File not found or not open: {ex.Message}", null));
                    continue;
                }

                if (targetBuffer == null)
                {
                    result.FailedEdits.Add(new FailedEdit(edit.FilePath ?? "", edit.Search ?? "", "Buffer resolver returned null.", null));
                    continue;
                }

                // Check version conflict for the primary file
                if (edit.FilePath.Equals(preEditState.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    if (targetBuffer.CurrentSnapshot.Version.VersionNumber != preEditState.VersionNumber)
                    {
                        // In a more advanced implementation, this would use the Document Version Guard logic to adjust spans.
                        // For now, any typing during generation is a hard version conflict.
                        result.Status = EditResultStatus.VersionConflict;
                        result.FailedEdits.Add(new FailedEdit(edit.FilePath ?? "", edit.Search ?? "", "File was modified by user during AI generation.", null));
                        return result;
                    }
                }

                string sourceText = targetBuffer.CurrentSnapshot.GetText();
                var match = _matchFinder.FindMatch(sourceText, edit.Search ?? "");

                if (match.Type == MatchType.None || match.ConfidenceScore < 0.8)
                {
                    result.FailedEdits.Add(new FailedEdit(edit.FilePath ?? "", edit.Search ?? "", "Search text not found or confidence too low.", match.ConfidenceScore));
                }
                else
                {
                    validatedEdits.Add((edit, targetBuffer, match));
                }
            }

            // If ANY edit failed validation, abort the entire transaction to guarantee atomicity
            if (result.FailedEdits.Count > 0)
            {
                result.Status = EditResultStatus.PartialSuccess; // Means some failed, nothing applied
                return result;
            }

            // Phase 2: Application
            // Group by buffer so we can apply edits from bottom to top to avoid shifting offsets
            var groupedEdits = validatedEdits
                .GroupBy(x => x.buffer)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.match.Position).ToList());

            ITextUndoTransaction? singleUndo = null;

            try
            {
                foreach (var group in groupedEdits)
                {
                    var buffer = group.Key;
                    
                    if (_undoHistoryRegistry != null)
                    {
                        var history = _undoHistoryRegistry.GetHistory(buffer);
                        singleUndo = history?.CreateTransaction("AI Edits");
                    }

                    using (var edit = buffer.CreateEdit())
                    {
                        foreach (var item in group.Value)
                        {
                            var span = new Span(item.match.Position, item.match.Length);
                            edit.Replace(span, item.edit.Replace ?? "");

                            result.AppliedEdits.Add(new AppliedEdit(
                                item.edit.FilePath ?? "",
                                item.match.Position,
                                item.match.Length,
                                item.edit.Search ?? "",
                                item.edit.Replace ?? ""
                            ));
                        }
                        edit.Apply();
                    }

                    singleUndo?.Complete();
                }
            }
            catch (Exception ex)
            {
                singleUndo?.Cancel();
                
                result.Status = EditResultStatus.RetryableError;
                result.FailedEdits.Add(new FailedEdit("Unknown", "N/A", $"Error applying edits: {ex.Message}", null));
                result.AppliedEdits.Clear(); // Everything was rolled back
            }

            return result;
        }
    }
}
