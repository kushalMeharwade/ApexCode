using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ApexCode.Services;

/// <summary>Coalesces notifications; a command returning does not mean VS finished rebuilding its tree.</summary>
internal sealed class ExplorerRefreshScheduler : IDisposable
{
    private readonly object _gate = new object();
    private readonly Func<CancellationToken, Task> _dispatch;
    private readonly Action<Exception> _onError;
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private readonly Timer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int _quietMs;
    private readonly int _cooldownMs;
    private long _lastRequest;
    private long _nextDispatch;
    private int _deferrals;
    private bool _pending, _running, _disposed;

    public ExplorerRefreshScheduler(Func<CancellationToken, Task> dispatch, Action<Exception> onError,
        int quietMs = 800, int cooldownMs = 1500)
    {
        _dispatch = dispatch;
        _onError = onError;
        _quietMs = quietMs;
        _cooldownMs = cooldownMs;
        _timer = new Timer(_ => { _ = RunAsync(); }, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Request()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending = true;
            _lastRequest = _clock.ElapsedMilliseconds;
            Arm();
        }
    }

    public IDisposable Defer()
    {
        lock (_gate)
        {
            if (!_disposed) { _deferrals++; _timer.Change(Timeout.Infinite, Timeout.Infinite); }
            return new Deferral(this);
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _deferrals--;
            _lastRequest = _clock.ElapsedMilliseconds;
            Arm();
        }
    }

    private void Arm()
    {
        if (_disposed || !_pending || _running || _deferrals != 0) return;
        var due = Math.Max(_lastRequest + _quietMs, _nextDispatch) - _clock.ElapsedMilliseconds;
        _timer.Change((int)Math.Max(1, due), Timeout.Infinite);
    }

    // Recheck after the dispatcher reaches the UI thread: another tool may have
    // started while that callback was queued behind other Visual Studio work.
    public bool ShouldDispatch()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (_deferrals != 0 || _pending || _clock.ElapsedMilliseconds < _lastRequest + _quietMs)
            { _pending = true; return false; }
            return true;
        }
    }

    private async Task RunAsync()
    {
        lock (_gate)
        {
            if (_disposed || _running || !_pending || _deferrals != 0) return;
            if (_clock.ElapsedMilliseconds < Math.Max(_lastRequest + _quietMs, _nextDispatch)) { Arm(); return; }
            _pending = false;
            _running = true;
        }
        try { await _dispatch(_lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { try { _onError(ex); } catch { /* logging must not fault the timer */ } }
        finally
        {
            lock (_gate)
            {
                _running = false;
                _nextDispatch = _clock.ElapsedMilliseconds + _cooldownMs;
                Arm();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _pending = false; _timer.Dispose(); }
        _lifetime.Cancel();
    }

    private sealed class Deferral : IDisposable
    {
        private ExplorerRefreshScheduler? _owner;
        public Deferral(ExplorerRefreshScheduler owner) { _owner = owner; }
        public void Dispose() { Interlocked.Exchange(ref _owner, null)?.Release(); }
    }
}
