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
/// <item><b>Compile:</b> the oldest Queued session whose scene registry is ready is compiled.
///   A Queued session whose scene version has not been uploaded moves to WaitingForScene, where
///   it stays, outside the queue, until that version is uploaded (SceneUploadEndpoints moves it
///   back). Waiting sessions therefore never hold up the ones behind them.</item>
/// </list>
/// One session compiles at a time, on a pool thread, and is cancelled after
/// <see cref="ServerConfig.CompileTimeout"/>.
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

        // Queued sessions whose scene version is missing wait outside the queue.
        List<Session> missingScene = await db.Sessions
            .Where(s => s.Status == SessionStatus.Queued &&
                        !db.SceneRevisions.Any(r => r.DeveloperId == s.DeveloperId && r.SceneId == s.SceneId && r.Hash == s.SceneHash))
            .Take(500)
            .ToListAsync(cancellationToken);

        // Queued sessions whose scene version was uploaded but cannot be read will never compile.
        List<Session> unusableScene = await db.Sessions
            .Where(s => s.Status == SessionStatus.Queued &&
                        db.SceneRevisions.Any(r => r.DeveloperId == s.DeveloperId && r.SceneId == s.SceneId && r.Hash == s.SceneHash &&
                                                   r.Status == SceneProcessingStatus.Failed && !r.RegistryReady))
            .Take(500)
            .ToListAsync(cancellationToken);

        foreach (Session s in missingScene)
        {
            s.Status = SessionStatus.WaitingForScene;
        }

        foreach (Session s in unusableScene)
        {
            s.Status = SessionStatus.Failed;
            s.ErrorMessage = "The scene this session was recorded in could not be read, so it cannot be replayed.";
        }

        if (missingScene.Count + unusableScene.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        Session? next = await db.Sessions
            .Where(s => s.Status == SessionStatus.Queued &&
                        db.SceneRevisions.Any(r => r.DeveloperId == s.DeveloperId && r.SceneId == s.SceneId && r.Hash == s.SceneHash &&
                                                   r.RegistryReady && r.RegistryPath != null))
            .OrderBy(s => s.LastActivityUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (next is null)
        {
            return false;
        }

        SceneRevision revision = (await db.SceneRevisions.FindAsync([next.DeveloperId, next.SceneId, next.SceneHash], cancellationToken))!;
        try
        {
            await CompileAsync(next, revision, db, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The session (or its whole scene) was deleted while it compiled. Its replay file
            // was written into a directory the deletion already removed; remove it again.
            SceneDeletion.TryDeleteDirectory(paths.SessionFull(StoragePaths.SessionDir(next.DeveloperId, next.Id)));
            ConsoleLog.Info($"Compile worker: session {next.Id} was deleted while compiling; discarded its output.");
        }

        return true;
    }

    private async Task CompileAsync(Session session, SceneRevision revision, MoodyBluesDbContext db, CancellationToken cancellationToken)
    {
        session.Status = SessionStatus.Compiling;
        await db.SaveChangesAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(config.CompileTimeout);

        string replayRelative = Path.Combine(StoragePaths.SessionDir(session.DeveloperId, session.Id), StoragePaths.ReplayFile);
        try
        {
            SceneRegistry registry = await SceneRegistry.LoadAsync(paths.SceneFull(revision.RegistryPath!), timeout.Token);
            var started = DateTime.UtcNow;
            CompiledReplay replay = await SessionCompiler.CompileAsync(
                paths.SessionFull(session.RawLogPath),
                registry,
                paths.SessionFull(replayRelative),
                cancellationToken: timeout.Token);

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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            session.Status = SessionStatus.Failed;
            session.ErrorMessage = $"Compilation took longer than {config.CompileTimeout.TotalMinutes:F0} minutes and was stopped.";
            await db.SaveChangesAsync(CancellationToken.None);
            ConsoleLog.Warn($"Compile worker: session {session.Id} timed out.");
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
