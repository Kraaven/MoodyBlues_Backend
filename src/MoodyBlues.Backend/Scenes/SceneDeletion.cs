using Microsoft.EntityFrameworkCore;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Ingest;
using MoodyBlues.Backend.Logging;
using MoodyBlues.Backend.Storage;

namespace MoodyBlues.Backend.Scenes;

/// <summary>What a deletion removed, for the response and the log.</summary>
public sealed record DeletionResult(int Versions, int Sessions, long Bytes);

/// <summary>
/// Deletes scene versions and every session recorded in them: database rows and files. Live
/// recordings of those versions are closed first, so a game that is still streaming is cut off
/// rather than writing into a session that no longer exists. A session that is compiling at the
/// time is cleaned up by the compile worker when its save fails (see CompileWorker).
/// </summary>
public static class SceneDeletion
{
    private static readonly TimeSpan ConnectionCloseWait = TimeSpan.FromSeconds(5);

    /// <summary>Deletes the given versions of one scene. With <paramref name="deleteScene"/>, also the scene row.</summary>
    public static async Task<DeletionResult> DeleteAsync(
        MoodyBluesDbContext db,
        StoragePaths paths,
        ActiveConnections connections,
        string developerId,
        string sceneId,
        IReadOnlyCollection<SceneRevision> revisions,
        bool deleteScene)
    {
        var hashes = revisions.Select(r => r.Hash).ToHashSet(StringComparer.Ordinal);

        // A deleted scene loses every session, including ones waiting for a version that was never
        // uploaded. Deleting single versions only removes the sessions recorded in those versions.
        List<Session> sessions = await db.Sessions
            .Where(s => s.DeveloperId == developerId && s.SceneId == sceneId)
            .ToListAsync();
        if (!deleteScene)
        {
            sessions = sessions.Where(s => hashes.Contains(s.SceneHash)).ToList();
        }

        List<Guid> live = sessions.Where(s => s.Status == SessionStatus.Recording).Select(s => s.Id).ToList();
        if (live.Count > 0)
        {
            connections.Abort(live);
            DateTime giveUp = DateTime.UtcNow + ConnectionCloseWait;
            while (live.Any(connections.IsOpen) && DateTime.UtcNow < giveUp)
            {
                await Task.Delay(100);
            }
        }

        long bytes = sessions.Sum(s => s.RawLogSizeBytes + s.ReplaySizeBytes) +
                     revisions.Sum(r => r.RawSizeBytes + (r.OptimizedSizeBytes ?? 0));

        db.Sessions.RemoveRange(sessions);
        db.SceneRevisions.RemoveRange(revisions);
        if (deleteScene && await db.Scenes.FindAsync(developerId, sceneId) is { } scene)
        {
            db.Scenes.Remove(scene);
        }

        await db.SaveChangesAsync();

        // Files go after the rows, so a failure here leaves unreferenced files, never rows that
        // point at missing ones.
        foreach (Session s in sessions)
        {
            TryDeleteDirectory(paths.SessionFull(StoragePaths.SessionDir(s.DeveloperId, s.Id)));
        }

        foreach (SceneRevision r in revisions)
        {
            TryDeleteDirectory(paths.SceneFull(StoragePaths.SceneRevisionDir(r.DeveloperId, r.SceneId, r.Hash)));
        }

        if (deleteScene)
        {
            TryDeleteDirectory(paths.SceneFull(Path.Combine(developerId, sceneId)));
        }

        ConsoleLog.Info(
            $"Deleted {(deleteScene ? "scene" : "versions of")} {developerId}/{sceneId}: " +
            $"{revisions.Count} version(s), {sessions.Count} session(s), {bytes} bytes" +
            (live.Count > 0 ? $", {live.Count} live recording(s) closed." : "."));

        return new DeletionResult(revisions.Count, sessions.Count, bytes);
    }

    public static void TryDeleteDirectory(string fullPath)
    {
        try
        {
            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ConsoleLog.Warn($"Could not delete {fullPath}: {ex.Message}");
        }
    }
}
