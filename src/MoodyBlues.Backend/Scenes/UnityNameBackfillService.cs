using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Logging;
using MoodyBlues.Backend.Storage;

namespace MoodyBlues.Backend.Scenes;

/// <summary>
/// Runs once at startup: scenes uploaded before <see cref="Scene.UnityName"/> existed get it from their
/// latest revision's stored GLB. Idempotent (scenes that already have a name are skipped; ones whose
/// GLB has none are simply looked at again next start), and a failure is logged, never fatal.
/// </summary>
public sealed class UnityNameBackfillService(IServiceScopeFactory scopeFactory, StoragePaths paths) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MoodyBluesDbContext>();
            int filled = await RunAsync(db, paths, stoppingToken);
            if (filled > 0)
            {
                ConsoleLog.Info($"Unity name backfill: named {filled} scene(s).");
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            ConsoleLog.Error("Unity name backfill failed.", ex);
        }
    }

    /// <summary>Fills every scene without a Unity name that has a readable GLB; returns how many were filled.</summary>
    public static async Task<int> RunAsync(MoodyBluesDbContext db, StoragePaths paths, CancellationToken cancellationToken)
    {
        List<Scene> scenes = await db.Scenes.Where(s => s.UnityName == null).ToListAsync(cancellationToken);
        int filled = 0;
        foreach (Scene scene in scenes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                SceneRevision? revision = await db.SceneRevisions.FindAsync([scene.ProjectId, scene.SceneId, scene.LatestHash], cancellationToken);
                if (revision is null)
                {
                    continue;
                }

                // The optimizer keeps the scene name, so either file will do; the raw one is the original.
                string? name = ReadFirst(paths, revision.RawGlbPath, revision.OptimizedGlbPath);
                if (name is null)
                {
                    continue;
                }

                scene.UnityName = name;
                await db.SaveChangesAsync(cancellationToken);
                filled++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                ConsoleLog.Error($"Unity name backfill: scene {scene.SceneId} of project {scene.ProjectId} failed.", ex);
            }
        }

        return filled;
    }

    private static string? ReadFirst(StoragePaths paths, params string?[] relativePaths)
    {
        foreach (string? relative in relativePaths)
        {
            if (relative is null)
            {
                continue;
            }

            string full = paths.SceneFull(relative);
            if (File.Exists(full) && GlbSceneName.TryReadFile(full) is { } name)
            {
                return name;
            }
        }

        return null;
    }
}
