using AiAssistant.Core.Models;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace AiAssistant.Core.Services;

public class ApprovalService : IApprovalService
{
    private readonly ConcurrentDictionary<string, ApprovalRequest> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, Channel<ApprovalRequest>> _requestChannels = new();

    public event EventHandler<ApprovalRequest>? ApprovalRequested;

    public Task<ApprovalRequest> RequestApprovalAsync(string toolName, string description, object? parameters = null)
    {
        var request = new ApprovalRequest
        {
            ToolName = toolName,
            Description = description,
            Parameters = parameters
        };

        // Pre-create channel BEFORE firing event to eliminate the race where auto-approve
        // (called synchronously from the event handler) writes to a channel that doesn't exist yet.
        var channel = Channel.CreateBounded<ApprovalRequest>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false
        });
        _requestChannels[request.Id] = channel;
        _pendingRequests[request.Id] = request;

        try
        {
            ApprovalRequested?.Invoke(this, request);
        }
        catch (Exception)
        {
            // A subscriber threw synchronously — clean up and let the exception propagate.
            // The tool function will receive an exception rather than hanging on WaitForApprovalAsync.
            _requestChannels.TryRemove(request.Id, out _);
            _pendingRequests.TryRemove(request.Id, out _);
            throw;
        }

        return Task.FromResult(request);
    }

    public Task ApproveAsync(string requestId)
    {
        if (_pendingRequests.TryGetValue(requestId, out var request))
        {
            var updated = request with { IsApproved = true };
            _pendingRequests[requestId] = updated;
            if (_requestChannels.TryGetValue(requestId, out var channel))
            {
                channel.Writer.TryWrite(updated);
            }
        }
        return Task.CompletedTask;
    }

    public Task RejectAsync(string requestId)
    {
        if (_pendingRequests.TryGetValue(requestId, out var request))
        {
            var updated = request with { IsRejected = true };
            _pendingRequests[requestId] = updated;
            if (_requestChannels.TryGetValue(requestId, out var channel))
            {
                channel.Writer.TryWrite(updated);
            }
        }
        return Task.CompletedTask;
    }

    public Task<ApprovalRequest?> GetPendingRequestAsync(string requestId)
    {
        _pendingRequests.TryGetValue(requestId, out var request);
        return Task.FromResult<ApprovalRequest?>(request);
    }

    public async IAsyncEnumerable<ApprovalRequest> WaitForApprovalAsync(string requestId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Channel was pre-created in RequestApprovalAsync.
        if (!_requestChannels.TryGetValue(requestId, out var channel))
            yield break; // Request already resolved or never registered cleanly.

        var reader = channel.Reader;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ApprovalRequest request;
                try
                {
                    request = await reader.ReadAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // User cancelled — exit cleanly
                    yield break;
                }
                yield return request;
                if (request.IsApproved || request.IsRejected)
                    break;
            }
        }
        finally
        {
            _requestChannels.TryRemove(requestId, out _);
            _pendingRequests.TryRemove(requestId, out _);
        }
    }
}
