using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Ingest;
using MoodyBlues.Backend.Logging;
using MoodyBlues.Backend.Scenes;
using MoodyBlues.Backend.Storage;

namespace MoodyBlues.Backend.Compiler;

/// <summary>
/// Moves sessions from Recording to Ready. The database is the queue, so the worker is
/// restart-safe: on startup, sessions left in Compiling are re-queued.
/// <list type="number">
/// <item><b>Seal:</b> a Recording session with no open connection and no activity for
///   <see cref="ServerConfig.ReconnectGracePeriod"/> is Queued (the client is not coming back).</item>
/// <item><b>Compile:</b> the oldest Queued session whose scene registry is ready is compiled. A
///   session whose scene revision never arrives fails after
///   <see cref="ServerConfig.SceneUploadWaitLimit"/>.</item>
/// </list>
/// One session compiles at a time; compilation runs off the request path, on a pool thread.
/// </summary>
public sealed class CompileWorker(
    IServiceScopeFactory scopeFactory,
    ServerConfig config,
    StoragePaths paths,
    ActiveConnections connections,
    CompileWorkSignal signal) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SealAbandonedAsync(DateTime.UtcNow, stoppingToken);
                if (await CompileNextAsync(DateTime.UtcNow, stoppingToken))
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ConsoleLog.Error("Compile worker: unexpected failure.", ex);
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
        List<Session> stuck = await db.Sessions.Where(s => s.Status == SessionStatus.Compiling).ToListAsync(cancellationToken);
        foreach (Session s in stuck)
        {
            s.Status = SessionStatus.Queued;
        }

        if (stuck.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            ConsoleLog.Info($"Compile worker: re-queued {stuck.Count} session(s) interrupted by a restart.");
        }
    }

    /// <summary>Queues Recording sessions whose client has gone. Public for tests.</summary>
    public async Task<int> SealAbandonedAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MoodyBluesDbContext>();
        DateTime cutoff = nowUtc - config.ReconnectGracePeriod;
        List<Session> candidates = await db.Sessions
            .Where(s => s.Status == SessionStatus.Recording && s.LastActivityUtc < cutoff)
            .ToListAsync(cancellationToken);

        int sealedCount = 0;
        foreach (Session s in candidates.Where(s => !connections.IsOpen(s.Id)))
        {
            if (!File.Exists(paths.SessionFull(s.RawLogPath)))
            {
                // Handshake happened but the client never streamed anything.
                s.Status = SessionStatus.Failed;
                s.ErrorMessage = "No data was received for this session.";
            }
            else
            {
                s.Status = SessionStatus.Queued;
            }

            sealedCount++;
        }

        if (sealedCount > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return sealedCount;
    }

    /// <summary>Compiles the oldest ready-to-compile session. Public for tests.</summary>
    public async Task<bool> CompileNextAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MoodyBluesDbContext>();

        List<Session> queued = await db.Sessions
            .Where(s => s.Status == SessionStatus.Queued)
            .OrderBy(s => s.LastActivityUtc)
            .Take(50)
            .ToListAsync(cancellationToken);

        foreach (Session session in queued)
        {
            SceneRevision? revision = await db.SceneRevisions.FindAsync(
                [session.DeveloperId, session.SceneId, session.SceneHash], cancellationToken);

            if (revision is { RegistryReady: true, RegistryPath: not null })
            {
                await CompileAsync(session, revision, db, cancellationToken);
                return true;
            }

            bool revisionUnusable = revision is { Status: SceneProcessingStatus.Failed, RegistryReady: false };
            bool waitedTooLong = revision is null && nowUtc - session.LastActivityUtc > config.SceneUploadWaitLimit;
            if (revisionUnusable || waitedTooLong)
            {
                session.Status = SessionStatus.Failed;
                session.ErrorMessage = revisionUnusable
                    ? "The scene this session was recorded in could not be read, so it cannot be replayed."
                    : "The scene this session was recorded in was never uploaded, so it cannot be replayed.";
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
        }

        return false;
    }

    private async Task CompileAsync(Session session, SceneRevision revision, MoodyBluesDbContext db, CancellationToken cancellationToken)
    {
        session.Status = SessionStatus.Compiling;
        await db.SaveChangesAsync(cancellationToken);

        string replayRelative = Path.Combine(StoragePaths.SessionDir(session.DeveloperId, session.Id), StoragePaths.ReplayFile);
        try
        {
            SceneRegistry registry = await SceneRegistry.LoadAsync(paths.SceneFull(revision.RegistryPath!), cancellationToken);
            var started = DateTime.UtcNow;
            CompiledReplay replay = await SessionCompiler.CompileAsync(
                paths.SessionFull(session.RawLogPath),
                registry,
                paths.SessionFull(replayRelative),
                cancellationToken: cancellationToken);

            session.ReplayPath = replayRelative;
            session.ReplaySizeBytes = new FileInfo(paths.SessionFull(replayRelative)).Length;
            session.DurationSeconds = replay.DurationSeconds;
            session.ObjectCount = replay.Objects.Count;
            session.SpawnCount = replay.SpawnCount;
            session.RawEventCount = replay.RawEventCount;
            session.KeyframeCount = replay.KeyframeCount;
            session.DeltaPromotions = replay.DeltaPromotions;
            session.HardClamps = replay.HardClamps;
            session.DroppedEvents = replay.DroppedEvents;
            session.EndedCleanly = session.EndedCleanly || replay.EndedCleanly;
            session.CompilerVersion = SessionCompiler.Version;
            session.CompiledAtUtc = DateTime.UtcNow;
            session.ErrorMessage = replay.UnknownObjectEvents > 0
                ? $"{replay.UnknownObjectEvents} transform event(s) referenced objects missing from the scene and were ignored."
                : null;
            session.Status = SessionStatus.Ready;
            await db.SaveChangesAsync(cancellationToken);

            ConsoleLog.Info(
                $"Compiled session {session.Id}: {replay.DurationSeconds:F1}s, {replay.Objects.Count} objects, " +
                $"{replay.RawEventCount} events -> {replay.KeyframeCount} keys, {session.ReplaySizeBytes} bytes " +
                $"in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            session.Status = SessionStatus.Failed;
            session.ErrorMessage = $"Compilation failed: {ex.Message}";
            await db.SaveChangesAsync(CancellationToken.None);
            ConsoleLog.Error($"Compile worker: session {session.Id} failed.", ex);
        }
    }
}
