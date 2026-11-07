using System.Collections.Concurrent;

namespace MoodyBlues.Backend.Ingest;

/// <summary>
/// In-memory registry of open <c>/stream</c> connections per session. The compile worker uses it
/// to never seal a session that is still streaming, and scene deletion uses it to close a
/// session's live connections. After a restart it is empty, which is correct: no connection
/// survives a restart.
/// </summary>
public sealed class ActiveConnections
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<int, CancellationTokenSource>> _open = new();
    private int _nextHandle;

    /// <summary>
    /// Registers a connection. Cancelling <paramref name="abort"/> is how <see cref="Abort"/>
    /// closes it. Dispose the result when the connection ends.
    /// </summary>
    public IDisposable Opened(Guid sessionId, CancellationTokenSource abort)
    {
        int handle = Interlocked.Increment(ref _nextHandle);
        _open.GetOrAdd(sessionId, _ => new ConcurrentDictionary<int, CancellationTokenSource>())[handle] = abort;
        return new Registration(this, sessionId, handle);
    }

    public bool IsOpen(Guid sessionId) => _open.TryGetValue(sessionId, out var set) && !set.IsEmpty;

    /// <summary>Closes every open connection of these sessions. Returns how many were closed.</summary>
    public int Abort(IEnumerable<Guid> sessionIds)
    {
        int closed = 0;
        foreach (Guid id in sessionIds)
        {
            if (!_open.TryGetValue(id, out var set)) continue;
            foreach (CancellationTokenSource abort in set.Values)
            {
                try
                {
                    abort.Cancel();
                    closed++;
                }
                catch (ObjectDisposedException)
                {
                    // The connection finished in the meantime.
                }
            }
        }

        return closed;
    }

    private void Closed(Guid sessionId, int handle)
    {
        if (!_open.TryGetValue(sessionId, out var set)) return;
        set.TryRemove(handle, out _);
        if (set.IsEmpty)
        {
            _open.TryRemove(new KeyValuePair<Guid, ConcurrentDictionary<int, CancellationTokenSource>>(sessionId, set));
        }
    }

    private sealed class Registration(ActiveConnections owner, Guid sessionId, int handle) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Closed(sessionId, handle);
            }
        }
    }
}
