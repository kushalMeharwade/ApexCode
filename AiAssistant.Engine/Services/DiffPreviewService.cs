using AiAssistant.Engine.Models;

namespace AiAssistant.Engine.Services;

public class DiffPreviewService : IDiffPreviewService
{
    public Task<DiffPreviewResult> GeneratePreviewAsync(string filePath, string oldContent, string newContent)
    {
        if (string.IsNullOrEmpty(oldContent) && string.IsNullOrEmpty(newContent))
        {
            return Task.FromResult(new DiffPreviewResult
            {
                FilePath = filePath,
                OriginalContent = oldContent,
                NewContent = newContent,
                IsValid = true,
                Changes = Array.Empty<string>(),
                Blocks = Array.Empty<DiffBlock>(),
                LinesAdded = 0,
                LinesRemoved = 0,
                LinesModified = 0,
                LinesUnchanged = 0
            });
        }

        var oldLines = (oldContent ?? "").Split('\n');
        var newLines = (newContent ?? "").Split('\n');

        // Compute LCS-based diff
        var diffLines = ComputeDiff(oldLines, newLines);
        var blocks = BuildBlocks(diffLines);

        var linesAdded = diffLines.Count(l => l.Type == DiffLineType.Added);
        var linesRemoved = diffLines.Count(l => l.Type == DiffLineType.Removed);
        var linesUnchanged = diffLines.Count(l => l.Type == DiffLineType.Unchanged);
        var linesModified = Math.Min(linesAdded, linesRemoved);

        // Build raw diff string for backward compatibility
        var rawChanges = diffLines.Select(l => l.Type switch
        {
            DiffLineType.Added => $"+ {l.Content}",
            DiffLineType.Removed => $"- {l.Content}",
            _ => $"  {l.Content}"
        });

        return Task.FromResult(new DiffPreviewResult
        {
            FilePath = filePath,
            OriginalContent = oldContent,
            NewContent = newContent,
            IsValid = true,
            Changes = rawChanges,
            Blocks = blocks,
            LinesAdded = linesAdded,
            LinesRemoved = linesRemoved,
            LinesModified = linesModified,
            LinesUnchanged = linesUnchanged
        });
    }

    public Task<bool> ApplyChangesAsync(DiffPreviewResult preview)
    {
        if (preview == null)
            return Task.FromResult(false);
        if (string.IsNullOrEmpty(preview.FilePath))
            return Task.FromResult(false);

        try
        {
            var directory = Path.GetDirectoryName(preview.FilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            AiAssistant.Storage.SafeFileWriter.WriteAllText(preview.FilePath, preview.NewContent ?? string.Empty);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] DiffPreviewService.ApplyChanges failed: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    public event EventHandler<DiffPreviewResult>? DiffProposed;
    private TaskCompletionSource<bool>? _approvalTcs;

    public async Task<bool> WaitForUserApprovalAsync(DiffPreviewResult preview)
    {
        _approvalTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        DiffProposed?.Invoke(this, preview);
        return await _approvalTcs.Task;
    }

    public void ResolveApproval(bool approved)
    {
        _approvalTcs?.TrySetResult(approved);
    }

    /// <summary>
    /// Clears any pending diff state. Called when the chat turn resets between API requests.
    /// </summary>
    public void Reset()
    {
        _approvalTcs?.TrySetCanceled();
        _approvalTcs = null;
    }

    /// <summary>
    /// Rejects any pending diff and cancels the pending approval. Called on cancellation / session end.
    /// </summary>
    public void RevertChanges()
    {
        _approvalTcs?.TrySetResult(false);
        _approvalTcs = null;
    }

    /// <summary>
    /// Computes a line-by-line diff using a simplified LCS (Longest Common Subsequence) algorithm.
    /// Produces a list of DiffLine with proper line numbers.
    /// </summary>
    public string ComputeDiff(string oldContent, string newContent)
    {
        var oldLines = (oldContent ?? "").Split('\n');
        var newLines = (newContent ?? "").Split('\n');
        var diffLines = ComputeDiff(oldLines, newLines);

        return string.Join("\n", diffLines.Select(l => l.Type switch
        {
            DiffLineType.Added => $"+ {l.Content}",
            DiffLineType.Removed => $"- {l.Content}",
            _ => $"  {l.Content}"
        }));
    }

    private static List<DiffLine> ComputeDiff(string[] oldLines, string[] newLines)
    {
        var oldContent = string.Join("\n", oldLines);
        var newContent = string.Join("\n", newLines);
        var diffBuilder = new DiffPlex.DiffBuilder.InlineDiffBuilder(new DiffPlex.Differ());
        var diffModel = diffBuilder.BuildDiffModel(oldContent, newContent);

        var result = new List<DiffLine>();
        int oldLine = 1, newLine = 1;

        foreach (var line in diffModel.Lines)
        {
            if (line.Type == DiffPlex.DiffBuilder.Model.ChangeType.Imaginary)
                continue;

            DiffLineType type = DiffLineType.Unchanged;
            int? origNum = null;
            int? newNum = null;

            switch (line.Type)
            {
                case DiffPlex.DiffBuilder.Model.ChangeType.Inserted:
                    type = DiffLineType.Added;
                    newNum = newLine++;
                    break;
                case DiffPlex.DiffBuilder.Model.ChangeType.Deleted:
                    type = DiffLineType.Removed;
                    origNum = oldLine++;
                    break;
                case DiffPlex.DiffBuilder.Model.ChangeType.Modified:
                    type = DiffLineType.Added;
                    newNum = newLine++;
                    break;
                case DiffPlex.DiffBuilder.Model.ChangeType.Unchanged:
                    type = DiffLineType.Unchanged;
                    origNum = oldLine++;
                    newNum = newLine++;
                    break;
            }

            result.Add(new DiffLine 
            { 
                Content = line.Text ?? string.Empty,
                Type = type,
                OriginalLineNumber = origNum,
                NewLineNumber = newNum
            });
        }

        return result;
    }

    private static List<DiffBlock> BuildBlocks(List<DiffLine> diffLines)
    {
        var blocks = new List<DiffBlock>();
        if (diffLines.Count == 0) return blocks;

        var currentLines = new List<DiffLine>();
        int blockOldStart = 1, blockNewStart = 1;
        int oldLineCounter = 1, newLineCounter = 1;
        bool inChangeBlock = false;

        for (int i = 0; i < diffLines.Count; i++)
        {
            var line = diffLines[i];

            if (line.Type == DiffLineType.Unchanged)
            {
                if (inChangeBlock && currentLines.Count > 0)
                {
                    // End the current change block
                    blocks.Add(CreateBlock(currentLines, blockOldStart, blockNewStart));
                    currentLines.Clear();
                    inChangeBlock = false;
                }
                oldLineCounter++;
                newLineCounter++;
            }
            else
            {
                if (!inChangeBlock)
                {
                    // Start a new change block — include 3 lines of context before
                    blockOldStart = Math.Max(1, oldLineCounter);
                    blockNewStart = Math.Max(1, newLineCounter);
                    inChangeBlock = true;

                    // Add context lines before (up to 3)
                    var contextStart = Math.Max(0, i - 3);
                    for (int c = contextStart; c < i; c++)
                    {
                        currentLines.Add(diffLines[c]);
                    }
                }
                currentLines.Add(line);

                if (line.Type == DiffLineType.Removed) oldLineCounter++;
                if (line.Type == DiffLineType.Added) newLineCounter++;
            }
        }

        // Don't forget the last block
        if (currentLines.Count > 0)
        {
            blocks.Add(CreateBlock(currentLines, blockOldStart, blockNewStart));
        }

        return blocks;
    }

    private static DiffBlock CreateBlock(List<DiffLine> lines, int oldStart, int newStart)
    {
        var oldCount = lines.Count(l => l.Type != DiffLineType.Added);
        var newCount = lines.Count(l => l.Type != DiffLineType.Removed);

        return new DiffBlock
        {
            OriginalStartLine = oldStart,
            OriginalLineCount = oldCount,
            NewStartLine = newStart,
            NewLineCount = newCount,
            Lines = lines.AsReadOnly()
        };
    }
}
