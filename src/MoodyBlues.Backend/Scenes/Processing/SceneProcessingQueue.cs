using System.Threading.Channels;

namespace MoodyBlues.Backend.Scenes.Processing;

/// <summary>One optimization job: run the raw upload for <c>(DeveloperId, SceneId)</c> through the Draco/KTX2 pipeline.</summary>
public sealed record SceneProcessingJob(string DeveloperId, string SceneId);

/// <summary>
/// In-memory producer/consumer queue between <see cref="SceneUploadEndpoints"/> (producer, on every
/// upload) and <see cref="SceneProcessingWorker"/> (sole consumer). Deliberately simple -- jobs are
/// lost on restart, which is fine: a scene that never got optimized just keeps serving its raw upload
/// (see <see cref="SceneEndpoints.DownloadAsync"/>) until the next re-upload retries the whole thing.
/// </summary>
public sealed class SceneProcessingQueue
{
    private readonly Channel<SceneProcessingJob> _channel = Channel.CreateUnbounded<SceneProcessingJob>();

    public void Enqueue(string developerId, string sceneId) =>
        _channel.Writer.TryWrite(new SceneProcessingJob(developerId, sceneId));

    public IAsyncEnumerable<SceneProcessingJob> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Non-blocking dequeue, used by tests to assert a job was enqueued without spinning up the worker.</summary>
    public bool TryRead(out SceneProcessingJob? job) => _channel.Reader.TryRead(out job);
}
