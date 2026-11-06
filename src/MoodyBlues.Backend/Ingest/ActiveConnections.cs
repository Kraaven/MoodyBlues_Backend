using System.Collections.Concurrent;

namespace MoodyBlues.Backend.Ingest;

/// <summary>
/// In-memory count of open <c>/stream</c> connections per session, so the compile worker never
/// seals a session that is still streaming. After a restart the count is zero for everything,
/// which is correct: no connection survives a restart.
/// </summary>
public sealed class ActiveConnections
{
    private readonly ConcurrentDictionary<Guid, int> _open = new();

    public void Opened(Guid sessionId) => _open.AddOrUpdate(sessionId, 1, (_, n) => n + 1);

    public void Closed(Guid sessionId)
    {
        while (_open.TryGetValue(sessionId, out int n))
        {
            if (n <= 1)
            {
                if (_open.TryRemove(new KeyValuePair<Guid, int>(sessionId, n)))
                {
                    return;
                }
            }
            else if (_open.TryUpdate(sessionId, n - 1, n))
            {
                return;
            }
        }
    }

    public bool IsOpen(Guid sessionId) => _open.ContainsKey(sessionId);
}
