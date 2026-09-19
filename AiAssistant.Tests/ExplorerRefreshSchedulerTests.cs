using System;
using System.Threading;
using System.Threading.Tasks;
using ApexCode.Services;
using Xunit;

namespace AiAssistant.Tests;

public class ExplorerRefreshSchedulerTests
{
    private static TaskCompletionSource<bool> Signal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Wait(Task task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(3000)));
        await task;
    }

    [Fact]
    public async Task WatcherAndToolBurstsAreCoalescedUntilOperationFinishes()
    {
        int calls = 0;
        var dispatched = Signal();
        using var scheduler = new ExplorerRefreshScheduler(_ =>
        { Interlocked.Increment(ref calls); dispatched.TrySetResult(true); return Task.CompletedTask; }, _ => { }, 40, 80);
        var operation = scheduler.Defer();
        for (int i = 0; i < 20; i++) scheduler.Request();
        await Task.Delay(100);
        Assert.Equal(0, calls);
        operation.Dispose();
        await Wait(dispatched.Task);
        await Task.Delay(150);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RequestsDuringDispatchWaitAndProduceOneFollowUp()
    {
        int calls = 0;
        var first = Signal();
        var release = Signal();
        var second = Signal();
        using var scheduler = new ExplorerRefreshScheduler(async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1) { first.SetResult(true); await release.Task; }
            else second.TrySetResult(true);
        }, _ => { }, 30, 80);
        scheduler.Request();
        await Wait(first.Task);
        for (int i = 0; i < 20; i++) scheduler.Request();
        await Task.Delay(100);
        Assert.Equal(1, calls);
        release.SetResult(true);
        await Wait(second.Task);
        await Task.Delay(150);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ClosingWorkspaceCancelsPendingRefresh()
    {
        int calls = 0;
        var scheduler = new ExplorerRefreshScheduler(_ =>
        { Interlocked.Increment(ref calls); return Task.CompletedTask; }, _ => { }, 40, 80);
        using var operation = scheduler.Defer();
        scheduler.Request();
        scheduler.Dispose();
        operation.Dispose();
        scheduler.Request();
        await Task.Delay(150);
        Assert.Equal(0, calls);
    }
}
