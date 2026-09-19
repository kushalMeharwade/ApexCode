using AiAssistant.Engine.Models;

namespace AiAssistant.Engine.Services;

public interface IDiffPreviewService
{
    Task<DiffPreviewResult> GeneratePreviewAsync(string filePath, string oldContent, string newContent);
    Task<bool> ApplyChangesAsync(DiffPreviewResult preview);
    string ComputeDiff(string oldContent, string newContent);
    
    event EventHandler<DiffPreviewResult>? DiffProposed;
    Task<bool> WaitForUserApprovalAsync(DiffPreviewResult preview);
    void ResolveApproval(bool approved);
    
    void Reset();
    void RevertChanges();
}
