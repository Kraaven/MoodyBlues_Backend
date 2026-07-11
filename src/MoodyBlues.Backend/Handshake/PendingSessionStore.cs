using System.Collections.Concurrent;

namespace MoodyBlues.Backend.Handshake;

/// <summary>One handshake's worth of context, waiting to be claimed by the WebSocket connection that follows it.</summary>
public sealed record PendingSession(string DeveloperId, string SceneId, DateTime CreatedAtUtc);

/// <summary>
/// Correlates a completed <c>/handshake</c> call with the WebSocket
/// connection Unity opens right after it, via the shared <c>sessionId</c>.
/// Purely in-memory and short-lived -- nothing here is persisted, and a
/// periodic sweep expires entries that are never claimed (e.g. Unity crashed
/// before connecting, or a stale retry).
/// </summary>
public sealed class PendingSessionStore : IDisposable
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, PendingSession> _sessions = new();
    private readonly Timer _sweepTimer;

    public PendingSessionStore()
    {
        _sweepTimer = new Timer(_ => Sweep(), state: null, Ttl, Ttl);
    }

    /// <summary>Registers (or refreshes, on handshake retry) a pending session.</summary>
    public void Put(string sessionId, string developerId, string sceneId) =>
        _sessions[sessionId] = new PendingSession(developerId, sceneId, DateTime.UtcNow);

    /// <summary>Looks up and removes a pending session -- each session token is single-use.</summary>
    public bool TryTake(string sessionId, out PendingSession? session) => _sessions.TryRemove(sessionId, out session);

    private void Sweep()
    {
        DateTime cutoff = DateTime.UtcNow - Ttl;
        foreach (var (sessionId, session) in _sessions)
        {
            if (session.CreatedAtUtc < cutoff)
            {
                _sessions.TryRemove(sessionId, out _);
            }
        }
    }

    public void Dispose() => _sweepTimer.Dispose();
}
