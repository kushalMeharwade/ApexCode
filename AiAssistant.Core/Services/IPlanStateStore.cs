using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Models;

namespace AiAssistant.Core.Services;

/// <summary>
/// Durable storage for per-session plan review state.
/// </summary>
/// <remarks>
/// The proposed steps themselves already round-trip through the persisted
/// <c>plan_mode_respond</c> tool call, but the <em>review</em> layer on top of them (reviewer
/// comments, revision number, approved/awaiting status, last selected mode) has nowhere to live in
/// the conversation schema. This store keeps that layer so an approved plan remains viewable after
/// the overlay closes, after a session switch, and after an IDE restart.
/// </remarks>
public interface IPlanStateStore
{
    /// <summary>Loads the state for a session, or <c>null</c> when nothing has been stored.</summary>
    Task<PlanSessionState?> LoadAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Writes the state for a session. Implementations must not throw on I/O failure.</summary>
    Task SaveAsync(PlanSessionState state, CancellationToken cancellationToken = default);

    /// <summary>Removes any stored state for a session.</summary>
    Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default);
}
