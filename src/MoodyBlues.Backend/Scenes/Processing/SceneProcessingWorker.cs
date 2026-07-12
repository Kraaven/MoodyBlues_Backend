using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Logging;

namespace MoodyBlues.Backend.Scenes.Processing;

/// <summary>
/// Sole consumer of <see cref="SceneProcessingQueue"/>. For every enqueued job, runs the raw upload
/// through <see cref="IGltfOptimizer"/> (Draco-compresses geometry, converts textures to KTX2,
/// dedupes/prunes unused data -- see the frontend's <c>loadScene.ts</c>, which already wires up
/// DRACOLoader/KTX2Loader expecting exactly this) and records the result on the <see cref="Scene"/> row.
///
/// Runs jobs strictly one at a time (a single background loop) -- scene uploads are infrequent and this
/// keeps it simple, with no risk of two optimization passes fighting over the same output file.
/// Failures never block viewing: <see cref="Scenes.SceneEndpoints.DownloadAsync"/> always falls back to
/// the raw upload when <see cref="SceneProcessingStatus.Ready"/> hasn't been reached (or the optimized
/// file isn't actually present on disk).
/// </summary>
public sealed class SceneProcessingWorker(
    SceneProcessingQueue queue,
    IServiceScopeFactory scopeFactory,
    ServerConfig config) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (SceneProcessingJob job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MoodyBluesDbContext>();
                var optimizer = scope.ServiceProvider.GetRequiredService<IGltfOptimizer>();
                await ProcessJobAsync(job, db, optimizer, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ConsoleLog.Error($"Scene processing: unhandled failure for developer={job.DeveloperId} scene={job.SceneId}", ex);
            }
        }
    }

    /// <summary>Public so it can be exercised directly in tests with a fake <see cref="IGltfOptimizer"/>, without a running host/queue.</summary>
    public async Task ProcessJobAsync(SceneProcessingJob job, MoodyBluesDbContext db, IGltfOptimizer optimizer, CancellationToken cancellationToken = default)
    {
        Data.Scene? scene = await db.Scenes.FindAsync([job.DeveloperId, job.SceneId], cancellationToken);
        if (scene is null)
        {
            ConsoleLog.Warn($"Scene processing: skipping developer={job.DeveloperId} scene={job.SceneId} (no longer exists).");
            return;
        }

        scene.ProcessingStatus = SceneProcessingStatus.Processing;
        await db.SaveChangesAsync(cancellationToken);

        string rawFullPath = Path.Combine(config.ScenesDir, scene.RawGlbPath);
        string optimizedRelativePath = Path.Combine(job.DeveloperId, $"{job.SceneId}.optimized.glb");
        string optimizedFullPath = Path.Combine(config.ScenesDir, optimizedRelativePath);

        bool ok = await optimizer.OptimizeAsync(rawFullPath, optimizedFullPath, cancellationToken);

        // Re-fetch: the row may have been re-uploaded (new hash/RawGlbPath) while we were optimizing the old bytes.
        scene = await db.Scenes.FindAsync([job.DeveloperId, job.SceneId], cancellationToken);
        if (scene is null)
        {
            return;
        }

        if (ok && File.Exists(optimizedFullPath) && new FileInfo(optimizedFullPath).Length > 0)
        {
            scene.OptimizedGlbPath = optimizedRelativePath;
            scene.OptimizedSizeBytes = new FileInfo(optimizedFullPath).Length;
            scene.ProcessingStatus = SceneProcessingStatus.Ready;
            ConsoleLog.Info(
                $"Scene processing: developer={job.DeveloperId} scene={job.SceneId} done "
                + $"({scene.RawSizeBytes} -> {scene.OptimizedSizeBytes} bytes).");
        }
        else
        {
            scene.ProcessingStatus = SceneProcessingStatus.Failed;
            ConsoleLog.Warn($"Scene processing: developer={job.DeveloperId} scene={job.SceneId} failed -- serving raw upload instead.");
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
