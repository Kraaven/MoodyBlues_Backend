using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Logging;
using MoodyBlues.Backend.Storage;

namespace MoodyBlues.Backend.Scenes.Processing;

/// <summary>
/// Processes uploaded scene revisions one at a time, in upload order. The database is the queue:
/// a revision in <see cref="SceneProcessingStatus.Pending"/> is work to do, and on startup any
/// revision left in Processing by a crash is put back to Pending.
///
/// Two steps, in this order:
/// <list type="number">
/// <item>Build the object registry from the raw GLB. This is fast and is all the session compiler
///   needs, so it is done first and signals the compile worker straight away.</item>
/// <item>Optimize the GLB for the dashboard. Slow (texture compression); failure only means the
///   raw file is served instead.</item>
/// </list>
/// </summary>
public sealed class SceneProcessingWorker(
    IServiceScopeFactory scopeFactory,
    StoragePaths paths,
    SceneWorkSignal signal,
    CompileWorkSignal compileSignal) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool didWork = await ProcessNextAsync(stoppingToken);
                if (didWork)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ConsoleLog.Error("Scene processing: unexpected failure.", ex);
            }

            try
            {
                await signal.Signal.WaitAsync(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MoodyBluesDbContext>();
        List<SceneRevision> stuck = await db.SceneRevisions
            .Where(r => r.Status == SceneProcessingStatus.Processing)
            .ToListAsync(cancellationToken);
        foreach (SceneRevision r in stuck)
        {
            r.Status = SceneProcessingStatus.Pending;
        }

        if (stuck.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            ConsoleLog.Info($"Scene processing: re-queued {stuck.Count} revision(s) interrupted by a restart.");
        }
    }

    /// <summary>Processes the oldest pending revision, if any. Public for tests.</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MoodyBluesDbContext>();
        var optimizer = scope.ServiceProvider.GetRequiredService<IGltfOptimizer>();

        SceneRevision? revision = await db.SceneRevisions
            .Where(r => r.Status == SceneProcessingStatus.Pending)
            .OrderBy(r => r.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (revision is null)
        {
            return false;
        }

        await ProcessAsync(revision, db, optimizer, cancellationToken);
        return true;
    }

    public async Task ProcessAsync(SceneRevision revision, MoodyBluesDbContext db, IGltfOptimizer optimizer, CancellationToken cancellationToken)
    {
        string label = $"developer={revision.DeveloperId} scene={revision.SceneId} hash={revision.Hash[..8]}";
        revision.Status = SceneProcessingStatus.Processing;
        await db.SaveChangesAsync(cancellationToken);

        string dir = StoragePaths.SceneRevisionDir(revision.DeveloperId, revision.SceneId, revision.Hash);
        string rawFull = paths.SceneFull(revision.RawGlbPath);

        // Step 1: registry
        if (!revision.RegistryReady)
        {
            try
            {
                SceneRegistry registry = await Task.Run(() => SceneRegistry.BuildFromGlb(rawFull), cancellationToken);
                string registryRelative = Path.Combine(dir, StoragePaths.RegistryFile);
                await registry.SaveAsync(paths.SceneFull(registryRelative), cancellationToken);
                revision.RegistryPath = registryRelative;
                revision.RegistryReady = true;
                revision.TrackedObjectCount = registry.Objects.Count;
                await db.SaveChangesAsync(cancellationToken);
                compileSignal.Signal.Notify();
                ConsoleLog.Info($"Scene processing: {label} registry built ({registry.Objects.Count} tracked objects).");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                revision.Status = SceneProcessingStatus.Failed;
                revision.ErrorMessage = $"Could not read the GLB: {ex.Message}";
                await db.SaveChangesAsync(cancellationToken);
                compileSignal.Signal.Notify(); // sessions waiting on this revision can now fail fast
                ConsoleLog.Warn($"Scene processing: {label} is not a readable GLB: {ex.Message}");
                return;
            }
        }

        // Step 2: optimization
        string optimizedRelative = Path.Combine(dir, StoragePaths.OptimizedGlbFile);
        string optimizedFull = paths.SceneFull(optimizedRelative);
        bool ok = await optimizer.OptimizeAsync(rawFull, optimizedFull, cancellationToken);
        if (ok)
        {
            revision.OptimizedGlbPath = optimizedRelative;
            revision.OptimizedSizeBytes = new FileInfo(optimizedFull).Length;
            revision.Status = SceneProcessingStatus.Ready;
            revision.ErrorMessage = null;
            ConsoleLog.Info($"Scene processing: {label} optimized ({revision.RawSizeBytes} -> {revision.OptimizedSizeBytes} bytes).");
        }
        else
        {
            revision.Status = SceneProcessingStatus.Failed;
            revision.ErrorMessage = "Optimization failed; the raw upload is served instead.";
            ConsoleLog.Warn($"Scene processing: {label} optimization failed; serving the raw upload.");
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
