using AiAssistant.Core.Models;
using System.Threading.Channels;

namespace AiAssistant.Core.Services;

public interface IApprovalService
{
    event EventHandler<ApprovalRequest>? ApprovalRequested;
    Task<ApprovalRequest> RequestApprovalAsync(string toolName, string description, object? parameters = null);
    Task ApproveAsync(string requestId);
    Task RejectAsync(string requestId);
    Task<ApprovalRequest?> GetPendingRequestAsync(string requestId);
    IAsyncEnumerable<ApprovalRequest> WaitForApprovalAsync(string requestId, CancellationToken cancellationToken = default);
}
