using System;
using System.Threading;
using System.Threading.Tasks;

namespace AiAssistant.Core.Services
{
    /// <summary>
    /// Provides session-level or task-level concurrency locks to prevent overlapping AI loop executions.
    /// </summary>
    public interface ITaskLockService
    {
        /// <summary>
        /// Acquires a lock for the given session ID. Returns an IDisposable that releases the lock upon disposal.
        /// </summary>
        /// <param name="sessionId">The session ID to lock.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>An IDisposable lock.</returns>
        Task<IDisposable> AcquireLockAsync(string sessionId, CancellationToken ct = default);
    }
}
