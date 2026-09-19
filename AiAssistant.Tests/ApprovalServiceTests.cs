using AiAssistant.Core.Models;
using AiAssistant.Core.Services;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AiAssistant.Tests;

public class ApprovalServiceTests
{
    private readonly ApprovalService _service;

    public ApprovalServiceTests()
    {
        _service = new ApprovalService();
    }

    [Fact]
    public async Task RequestApprovalAsync_AddsRequestAndRaisesEvent()
    {
        bool eventRaised = false;
        _service.ApprovalRequested += (sender, args) =>
        {
            eventRaised = true;
            Assert.Equal("tool", args.ToolName);
            Assert.Equal("desc", args.Description);
        };

        var request = await _service.RequestApprovalAsync("tool", "desc");

        Assert.True(eventRaised);
        Assert.NotNull(request.Id);
        
        var pending = await _service.GetPendingRequestAsync(request.Id);
        Assert.NotNull(pending);
    }

    [Fact]
    public async Task ApproveAsync_UpdatesRequestStatus()
    {
        var request = await _service.RequestApprovalAsync("t", "d");

        await _service.ApproveAsync(request.Id);

        var pending = await _service.GetPendingRequestAsync(request.Id);
        Assert.NotNull(pending);
        Assert.True(pending.IsApproved);
        Assert.False(pending.IsRejected);
    }

    [Fact]
    public async Task RejectAsync_UpdatesRequestStatus()
    {
        var request = await _service.RequestApprovalAsync("t", "d");

        await _service.RejectAsync(request.Id);

        var pending = await _service.GetPendingRequestAsync(request.Id);
        Assert.NotNull(pending);
        Assert.False(pending.IsApproved);
        Assert.True(pending.IsRejected);
    }

    [Fact]
    public async Task WaitForApprovalAsync_YieldsWhenApproved()
    {
        var request = await _service.RequestApprovalAsync("t", "d");

        var waitTask = Task.Run(async () =>
        {
            await foreach (var req in _service.WaitForApprovalAsync(request.Id))
            {
                if (req.IsApproved) return true;
            }
            return false;
        });

        // Delay briefly to allow the wait to start
        await Task.Delay(50);
        await _service.ApproveAsync(request.Id);

        var result = await waitTask;
        Assert.True(result);
    }

    [Fact]
    public async Task WaitForApprovalAsync_HandlesCancellation()
    {
        var request = await _service.RequestApprovalAsync("t", "d");
        var cts = new CancellationTokenSource();

        var waitTask = Task.Run(async () =>
        {
            await foreach (var req in _service.WaitForApprovalAsync(request.Id, cts.Token))
            {
                // Should not reach here because we cancel before any events are written
            }
            return true; // We exited cleanly without throwing
        });

        await Task.Delay(50);
        cts.Cancel();

        var result = await waitTask;
        Assert.True(result);
    }
}
