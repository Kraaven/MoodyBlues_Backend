namespace MoodyBlues.Backend.Common;

/// <summary>
/// Wakes a background worker early. Workers poll the database on a timer anyway (the database
/// is the durable queue, so nothing is lost on restart); this only removes the polling delay
/// when an endpoint knows new work just arrived.
/// </summary>
public sealed class WorkSignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Notify()
    {
        try
        {
            if (_semaphore.CurrentCount == 0)
            {
                _semaphore.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
    }

    /// <summary>Waits until notified or until <paramref name="timeout"/> passes.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(timeout, cancellationToken);
    }
}

/// <summary>Signal for the scene worker (new upload).</summary>
public sealed class SceneWorkSignal
{
    public WorkSignal Signal { get; } = new();
}

/// <summary>Signal for the compile worker (session ended, or a scene registry became ready).</summary>
public sealed class CompileWorkSignal
{
    public WorkSignal Signal { get; } = new();
}
