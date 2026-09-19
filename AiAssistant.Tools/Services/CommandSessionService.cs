using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AiAssistant.Tools.Services;

/// <summary>Owns commands independently of individual tool invocations. Dispose stops all processes.</summary>
public sealed class CommandSessionService : IDisposable
{
    public static readonly AsyncLocal<string?> Owner = new();
    public static readonly AsyncLocal<string?> CallId = new();
    public event EventHandler<AiAssistant.Core.Services.CommandProgress>? Progress;
    private readonly AiAssistant.Core.Services.IVisualStudioEnvironmentService? _environment;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly object _gate = new();
    private bool _disposed;
    private static readonly string LogDirectory = Path.Combine(Path.GetTempPath(), "ApexCode", "CommandLogs");

    public CommandSessionService(AiAssistant.Core.Services.IVisualStudioEnvironmentService? environment = null)
    {
        _environment = environment;
        if (_environment != null) _environment.WorkspaceChanged += WorkspaceChanged;
        // Only remove our own expired log files, never workspace files or arbitrary paths.
        try
        {
            if (Directory.Exists(LogDirectory))
                foreach (var file in Directory.EnumerateFiles(LogDirectory, "*.log"))
                    if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out _) && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1))
                        try { File.Delete(file); } catch (IOException) { }
        }
        catch (Exception ex) { Debug.WriteLine("Command log retention: " + ex.Message); }
    }

    private void WorkspaceChanged(object? sender, EventArgs args)
    {
        // This event signals solution/folder changes, not individual file edits.
        foreach (var session in _sessions.Values) { session.Stop("cancelled"); session.TerminalObserved = true; }
    }

    public string Start(string command, string workspace, bool background, int timeoutSeconds, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CommandSessionService));
            ct.ThrowIfCancellationRequested();
            foreach (var old in _sessions.Values.Where(s => s.Done.IsCompleted && s.Finished < DateTime.UtcNow.AddHours(-1)).ToList())
                if (_sessions.TryRemove(old.Id, out _)) old.Dispose();
            if (_sessions.Count >= 64)
                foreach (var old in _sessions.Values.Where(s => s.Done.IsCompleted && s.TerminalObserved).OrderBy(s => s.Finished).ToList())
                {
                    if (_sessions.Count < 64) break;
                    if (_sessions.TryRemove(old.Id, out _)) old.Dispose();
                }
            if (_sessions.Count >= 64 || _sessions.Values.Count(s => !s.Done.IsCompleted) >= 8)
                throw new InvalidOperationException("Command session limit reached. Stop unused commands before starting another.");
            var owner = Owner.Value ?? "standalone";
            var existing = _sessions.Values.FirstOrDefault(s => s.Owner == owner && string.Equals(s.Workspace, workspace, StringComparison.OrdinalIgnoreCase) && s.Command == command && !s.Done.IsCompleted);
            if (existing != null)
                throw new InvalidOperationException("This command is already running in session " + existing.Id + ". Read that session instead of restarting it.");
            var session = new Session(command, workspace, owner, background, timeoutSeconds, ct);
            _sessions[session.Id] = session;
            _ = PublishAsync(session, CallId.Value ?? "");
            return session.Id;
        }
    }

    private async Task PublishAsync(Session session, string callId)
    {
        try
        {
            do
            {
                await Task.WhenAny(session.Done, Task.Delay(500)).ConfigureAwait(false);
                Progress?.Invoke(this, new AiAssistant.Core.Services.CommandProgress {
                    Owner = session.Owner, CallId = callId, Result = session.Read(Math.Max(0, session.Length - 16384))
                });
            } while (!session.Done.IsCompleted);
        }
        catch (Exception ex) { Debug.WriteLine("Command progress listener failed: " + ex.Message); }
    }

    private Session Get(string id, string workspace)
    {
        if (!_sessions.TryGetValue(id, out var session) || session.Owner != (Owner.Value ?? "standalone") ||
            !string.Equals(session.Workspace, workspace, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unknown command session for this chat and workspace.");
        return session;
    }

    public async Task<string> ReadAsync(string id, string workspace, long cursor, int waitMs, CancellationToken ct, bool waitForExit = false)
    {
        var s = Get(id, workspace);
        using var cancellation = ct.Register(() => s.Stop("cancelled"));
        if (cursor < 0) throw new ArgumentOutOfRangeException(nameof(cursor));
        // Long-poll until the wait expires or execution completes; noisy output does not cause a polling storm.
        if (!s.Done.IsCompleted && (waitForExit || cursor >= s.Length))
            await Task.WhenAny(s.Done, Task.Delay(waitMs, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return s.Read(cursor, true);
    }

    public async Task<string> StopAsync(string id, string workspace, CancellationToken ct)
    {
        var s = Get(id, workspace);
        s.Stop("cancelled");
        await Task.WhenAny(s.Done, Task.Delay(10000, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return s.Read(0, true);
    }

    public bool HasPendingBuilds(string owner) => _sessions.Values.Any(s => s.Owner == owner && !s.Background && !s.TerminalObserved);
    public string PendingSessionIds(string owner) => string.Join(", ", _sessions.Values.Where(s => s.Owner == owner && !s.Background && !s.TerminalObserved).Select(s => s.Id));
    public void Dispose()
    {
        lock (_gate) {
            _disposed = true;
            if (_environment != null) _environment.WorkspaceChanged -= WorkspaceChanged;
            foreach (var s in _sessions.Values) s.Dispose(); _sessions.Clear();
        }
    }

    private sealed class Session : IDisposable
    {
        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly string Command, Workspace, Owner;
        public readonly bool Background;
        private readonly Process _process;
        private readonly WindowsCommandJob _job;
        private readonly object _sync = new();
        private readonly StringBuilder _tail = new();
        private readonly StreamWriter _log;
        private readonly string _logPath;
        private long _offset;
        private string _status = "running";
        private string? _captureError;
        private int? _exitCode;
        private long _logCharacters;
        private bool _logStopped;
        private readonly Timer _timeout;
        private CancellationTokenRegistration _cancellation;
        public Task Done { get; }
        public DateTime Finished { get; private set; } = DateTime.MaxValue;
        public volatile bool TerminalObserved;
        public long Length { get { lock (_sync) return _offset + _tail.Length; } }

        public Session(string command, string workspace, string owner, bool background, int timeoutSeconds, CancellationToken ct)
        {
            Command = command; Workspace = workspace; Owner = owner; Background = background;
            var directory = LogDirectory;
            Directory.CreateDirectory(directory);
            _logPath = Path.Combine(directory, Id + ".log");
            // Fixed-width UTF-16 permits bounded reads at character cursors without rescanning
            // a large log from the beginning every time the in-memory tail rolls over.
            _log = new StreamWriter(new FileStream(_logPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), new UnicodeEncoding(false, false));
            try { _job = new WindowsCommandJob(); }
            catch { _log.Dispose(); throw; }
            // Gate the actual command on stdin until the shell belongs to the job, so even
            // very fast child processes inherit ownership. /d disables cmd AutoRun scripts.
            _process = new Process { StartInfo = new ProcessStartInfo("cmd.exe", "/d /q /c set /p APEX_COMMAND_GATE= >nul & " + command) {
                WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
            }, EnableRaisingEvents = true };
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _process.Exited += (_, __) => exited.TrySetResult(true);
            try
            {
                _process.Start();
                _job.Assign(_process);
                _process.StandardInput.WriteLine("ready");
                _process.StandardInput.Close();
            }
            catch { try { _process.Kill(); } catch { } _job.Dispose(); _process.Dispose(); _log.Dispose(); throw; }
            _timeout = new Timer(_ => Stop("timed_out"), null, TimeSpan.FromSeconds(timeoutSeconds), Timeout.InfiniteTimeSpan);
            _cancellation = ct.Register(() => Stop("cancelled"));
            Done = FinishAsync(exited.Task, PumpAsync(_process.StandardOutput, false), PumpAsync(_process.StandardError, true));
        }

        private async Task PumpAsync(StreamReader reader, bool stderr)
        {
            try
            {
                var buffer = new char[4096];
                int count;
                while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    var chunk = (stderr ? "[STDERR] " : "") + new string(buffer, 0, count);
                    lock (_sync)
                    {
                        _tail.Append(chunk);
                        if (_tail.Length > 131072) { int excess = _tail.Length - 131072; _tail.Remove(0, excess); _offset += excess; }
                        try {
                            if (!_logStopped && _logCharacters + chunk.Length <= 32 * 1024 * 1024) { _log.Write(chunk); _log.Flush(); _logCharacters += chunk.Length; }
                            else if (!_logStopped) { _logStopped = true; _captureError = "Full log limit (32 million characters) exceeded. Output is incomplete; do not claim successful verification."; }
                        }
                        catch (Exception ex) { _logStopped = true; _captureError = "Log write failed: " + ex.Message; }
                    }
                }
            }
            catch (Exception ex) { lock (_sync) _captureError = "Output capture failed: " + ex.Message; }
        }

        private async Task FinishAsync(Task exited, Task stdout, Task stderr)
        {
            await exited.ConfigureAwait(false);
            // A child retaining redirected handles is still part of this command's lifetime.
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            lock (_sync)
            {
                _exitCode = _process.ExitCode;
                if (_status == "running") _status = _exitCode == 0 && _captureError == null ? "completed" : "failed";
                Finished = DateTime.UtcNow;
                try { _log.Dispose(); }
                catch (Exception ex) { _captureError = "Log close failed: " + ex.Message; if (_status == "completed") _status = "failed"; }
            }
            _timeout.Dispose();
            _cancellation.Dispose();
            _job.Dispose();
            _process.Dispose();
        }

        public void Stop(string status)
        {
            lock (_sync)
            {
                if (_status != "running") return;
                _status = status;
                try { _job.Terminate(); }
                catch (Exception ex) { _captureError = "Process cleanup failed: " + ex.Message; _job.Dispose(); }
            }
        }

        public string Read(long cursor, bool observed = false)
        {
            lock (_sync)
            {
                var end = _offset + _tail.Length;
                if (cursor > end) throw new ArgumentOutOfRangeException(nameof(cursor), "Cursor is beyond available output.");
                bool recovered = cursor < _offset && _logCharacters >= Math.Min(end, cursor + 16384);
                var start = recovered ? cursor : Math.Max(cursor, _offset);
                var count = (int)Math.Min(16384, end - start);
                var next = start + count;
                string output;
                if (recovered)
                {
                    using var file = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    file.Seek(start * 2, SeekOrigin.Begin);
                    using var reader = new StreamReader(file, Encoding.Unicode, false);
                    var chars = new char[count];
                    int read = reader.ReadBlock(chars, 0, count);
                    output = new string(chars, 0, read);
                    next = start + read;
                }
                else output = _tail.ToString((int)(start - _offset), count);
                if (observed && _exitCode.HasValue && next == end) TerminalObserved = true;
                return JsonSerializer.Serialize(new {
                    session_id = Id, status = _status, background = Background,
                    success = _status == "running" ? (bool?)null : _status == "completed",
                    exit_code = _exitCode, output,
                    next_cursor = next, has_more = next < end, truncated = cursor < _offset && !recovered,
                    recovered_from_log = recovered,
                    log_path = _logPath, capture_error = _captureError,
                    next_action = _status == "running" ? "Read this session again; do not rerun the command. Running is not verification success. For servers verify readiness separately." :
                        next < end ? "Read remaining output using next_cursor before assessing results." : "Inspect exit_code and diagnostics. If truncated, inspect log_path before claiming verification."
                });
            }
        }

        public void Dispose() { Stop("cancelled"); /* FinishAsync drains and releases resources. */ }
    }
}
