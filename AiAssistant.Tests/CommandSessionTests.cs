using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Tools.Services;
using Xunit;

namespace AiAssistant.Tests;

public sealed class CommandSessionTests : IDisposable
{
    private readonly CommandSessionService _sessions = new();
    private readonly string _workspace = Path.GetTempPath();
    public CommandSessionTests() { CommandSessionService.Owner.Value = "command-tests"; }
    public void Dispose() => _sessions.Dispose();
    private async Task<JsonDocument> Read(string id, long cursor = 0, int wait = 1000) =>
        JsonDocument.Parse(await _sessions.ReadAsync(id, _workspace, cursor, wait, CancellationToken.None));

    [Fact]
    public async Task LongCommandYieldsThenReturnsLateCompilerErrors()
    {
        string id = _sessions.Start("powershell -NoProfile -Command \"Write-Output 'start'; Start-Sleep -Seconds 2; [Console]::Error.WriteLine('late compiler error'); exit 7\"", _workspace, false, 30, CancellationToken.None);
        using var first = await Read(id);
        Assert.Equal("running", first.RootElement.GetProperty("status").GetString());
        long cursor = first.RootElement.GetProperty("next_cursor").GetInt64();
        string output = first.RootElement.GetProperty("output").GetString();
        for (int i = 0; i < 12; i++)
        {
            using var next = await Read(id, cursor);
            output += next.RootElement.GetProperty("output").GetString();
            cursor = next.RootElement.GetProperty("next_cursor").GetInt64();
            if (next.RootElement.GetProperty("status").GetString() != "running")
            {
                Assert.Equal("failed", next.RootElement.GetProperty("status").GetString());
                Assert.Equal(7, next.RootElement.GetProperty("exit_code").GetInt32());
                Assert.Contains("late compiler error", output);
                Assert.Equal(1, output.Split(new[] { "late compiler error" }, StringSplitOptions.None).Length - 1);
                return;
            }
        }
        Assert.True(false, "Command did not finish in time.");
    }

    [Fact]
    public async Task TimeoutRetainsOutputAndDoesNotReportSuccess()
    {
        string id = _sessions.Start("echo before-timeout & ping -n 30 127.0.0.1", _workspace, false, 1, CancellationToken.None);
        await Task.Delay(1600);
        using var result = await Read(id);
        Assert.Equal("timed_out", result.RootElement.GetProperty("status").GetString());
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("before-timeout", result.RootElement.GetProperty("output").GetString());
    }

    [Fact]
    public async Task CancellationAfterYieldStopsTheSession()
    {
        using var cancellation = new CancellationTokenSource();
        string id = _sessions.Start("ping -n 30 127.0.0.1", _workspace, false, 30, cancellation.Token);
        using var first = await Read(id);
        cancellation.Cancel();
        await Task.Delay(300);
        using var result = await Read(id);
        Assert.Equal("cancelled", result.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task OwnershipAndDuplicateCommandsAreEnforced()
    {
        string id = _sessions.Start("ping -n 30 127.0.0.1", _workspace, true, 30, CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => _sessions.Start("ping -n 30 127.0.0.1", _workspace, true, 30, CancellationToken.None));
        CommandSessionService.Owner.Value = "another-chat";
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sessions.ReadAsync(id, _workspace, 0, 1000, CancellationToken.None));
        CommandSessionService.Owner.Value = "command-tests";
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sessions.ReadAsync(id, "different-workspace", 0, 1000, CancellationToken.None));
        using var result = JsonDocument.Parse(await _sessions.StopAsync(id, _workspace, CancellationToken.None));
        Assert.Equal("cancelled", result.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task LargeOutputIsBoundedAndFullLogRetainsDiagnostics()
    {
        string id = _sessions.Start("powershell -NoProfile -Command \"[Console]::Write(('x' * 200000)); [Console]::Error.WriteLine('final diagnostic')\"", _workspace, false, 30, CancellationToken.None);
        await Task.Delay(2500);
        using var result = await Read(id);
        Assert.False(result.RootElement.GetProperty("truncated").GetBoolean());
        Assert.True(result.RootElement.GetProperty("recovered_from_log").GetBoolean());
        Assert.True(result.RootElement.GetProperty("output").GetString().Length <= 16384);
        Assert.Contains("final diagnostic", File.ReadAllText(result.RootElement.GetProperty("log_path").GetString(), System.Text.Encoding.Unicode));
        Assert.True(_sessions.HasPendingBuilds("command-tests"));
        long cursor = result.RootElement.GetProperty("next_cursor").GetInt64();
        string remaining = result.RootElement.GetProperty("output").GetString();
        for (int page = 0; page < 30; page++)
        {
            using var next = await Read(id, cursor);
            remaining += next.RootElement.GetProperty("output").GetString();
            cursor = next.RootElement.GetProperty("next_cursor").GetInt64();
            if (!next.RootElement.GetProperty("has_more").GetBoolean()) break;
        }
        Assert.Contains("final diagnostic", remaining);
        Assert.Equal(200000, remaining.Count(c => c == 'x'));
        Assert.False(_sessions.HasPendingBuilds("command-tests"));
    }

    [Fact]
    public async Task StopTerminatesChildProcess()
    {
        string id = _sessions.Start("powershell -NoProfile -Command \"Write-Output $PID; Start-Sleep -Seconds 30\"", _workspace, true, 30, CancellationToken.None);
        using var initial = await Read(id, wait: 2000);
        int pid = int.Parse(initial.RootElement.GetProperty("output").GetString().Trim());
        await _sessions.StopAsync(id, _workspace, CancellationToken.None);
        try
        {
            using var child = System.Diagnostics.Process.GetProcessById(pid);
            Assert.True(child.HasExited);
        }
        catch (ArgumentException) { /* No process with that ID remains. */ }
    }

    [Fact]
    public async Task WorkspaceChangeStopsCommandsAndClearsCompletionGate()
    {
        var environment = new Moq.Mock<AiAssistant.Core.Services.IVisualStudioEnvironmentService>();
        using var sessions = new CommandSessionService(environment.Object);
        string id = sessions.Start("ping -n 30 127.0.0.1", _workspace, false, 30, CancellationToken.None);
        environment.Raise(e => e.WorkspaceChanged += null, EventArgs.Empty);
        using var result = JsonDocument.Parse(await sessions.ReadAsync(id, _workspace, 0, 1000, CancellationToken.None));
        Assert.Equal("cancelled", result.RootElement.GetProperty("status").GetString());
        Assert.False(sessions.HasPendingBuilds("command-tests"));
    }
}
