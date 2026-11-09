using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Logging;
using MoodyBlues.Backend.Storage;

namespace MoodyBlues.Backend.Scenes;

/// <summary>
/// <c>POST /scenes/{sceneId}?sceneHash=</c> (the project comes from the ingest key): a Unity client or the Unity editor
/// uploading the GLB of a scene version the server does not have (Unity Spec 13.3). The body is
/// the .glb, usually gzip-encoded. An optional <c>X-MoodyBlues-Skipped-Meshes</c> header reports
/// how many meshes a runtime export could not read.
///
/// Versions are immutable: if this hash is already stored, the upload is accepted and ignored.
/// Each upload writes its own file, and only the upload whose database row is inserted keeps it,
/// so two concurrent uploads of the same hash can never leave a row pointing at the other's file.
/// A new version becomes the scene's current version; the previous ones move to the archive.
/// </summary>
public static class SceneUploadEndpoints
{
    public const string SkippedMeshesHeader = "X-MoodyBlues-Skipped-Meshes";

    private const int MaxVersionAttempts = 5;

    public static void Map(WebApplication app)
    {
        app.MapPost("/scenes/{sceneId}", HandleAsync).RequireRateLimiting(RateLimits.Ingest);
    }

    /// <summary>Public so tests can call it directly.</summary>
    public static async Task<IResult> HandleAsync(
        string sceneId,
        HttpRequest request,
        MoodyBluesDbContext db,
        ServerConfig config,
        StoragePaths paths,
        SceneWorkSignal signal)
    {
        string? sceneHash = request.Query["sceneHash"];

        if (!PathSegments.IsSafe(sceneId) || !PathSegments.IsSceneHash(sceneHash))
        {
            return Results.BadRequest("sceneId and a 64-character lowercase hex sceneHash are required.");
        }

        (IngestProject? project, IResult? denied) = await IngestAccess.ResolveAsync(db, request);
        if (denied is not null)
        {
            return denied;
        }

        string developerId = project!.DeveloperId;

        if (await RevisionExistsAsync(db, developerId, sceneId, sceneHash!))
        {
            return Results.Ok(); // already have this version; versions never change
        }

        if (config.MaxRevisionsPerScene > 0 &&
            await db.SceneRevisions.CountAsync(r => r.DeveloperId == developerId && r.SceneId == sceneId) >= config.MaxRevisionsPerScene)
        {
            return Results.Problem(
                $"This scene already has {config.MaxRevisionsPerScene} stored versions. Delete archived versions in the dashboard to upload more.",
                statusCode: StatusCodes.Status507InsufficientStorage);
        }

        if (config.MaxStoragePerDeveloperBytes > 0 &&
            await StorageQuota.UsedBytesAsync(db, developerId) >= config.MaxStoragePerDeveloperBytes)
        {
            return Results.Problem(
                "This project has reached its storage limit. Delete archived versions or old sessions in the dashboard.",
                statusCode: StatusCodes.Status507InsufficientStorage);
        }

        int skippedMeshes = int.TryParse(request.Headers[SkippedMeshesHeader], out int skipped) && skipped > 0 ? skipped : 0;

        // The size cap is enforced on the decompressed stream we actually read, which is what
        // protects the disk from a gzip bomb. Kestrel's own limit is lifted for this request.
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
        {
            sizeFeature.MaxRequestBodySize = null;
        }

        // A file per upload, so a concurrent upload of the same hash cannot replace this one.
        string dir = StoragePaths.SceneRevisionDir(developerId, sceneId, sceneHash!);
        string rawRelative = Path.Combine(dir, $"raw-{Guid.NewGuid():N}.glb");
        string rawFull = paths.SceneFull(rawRelative);

        long written = 0;
        bool tooLarge = false, notGlb = false;
        try
        {
            await StoragePaths.WriteAtomicAsync(rawFull, async output =>
            {
                var buffer = new byte[81920];
                var magic = new byte[4];
                int magicRead = 0;
                int read;
                while ((read = await request.Body.ReadAsync(buffer)) > 0)
                {
                    // Collect the first four bytes across reads: a decompressing stream may hand
                    // them over in several pieces.
                    for (int i = 0; i < read && magicRead < 4; i++)
                    {
                        magic[magicRead++] = buffer[i];
                    }

                    if (magicRead == 4 && written < 4 && !magic.AsSpan().SequenceEqual("glTF"u8))
                    {
                        notGlb = true;
                        throw new InvalidDataException("not a GLB");
                    }

                    written += read;
                    if (written > config.MaxSceneUploadBytes)
                    {
                        tooLarge = true;
                        throw new InvalidDataException("too large");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read));
                }

                if (magicRead < 4)
                {
                    notGlb = true;
                    throw new InvalidDataException("empty");
                }
            });
        }
        catch (InvalidDataException) when (tooLarge)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        catch (InvalidDataException) when (notGlb)
        {
            return Results.BadRequest("The body is not a binary glTF (.glb) file.");
        }

        for (int attempt = 0; attempt < MaxVersionAttempts; attempt++)
        {
            DateTime now = DateTime.UtcNow;
            int version = 1 + (await db.SceneRevisions
                .Where(r => r.DeveloperId == developerId && r.SceneId == sceneId)
                .Select(r => (int?)r.Version)
                .MaxAsync() ?? 0);

            Scene? scene = await db.Scenes.FindAsync(developerId, sceneId);
            if (scene is null)
            {
                db.Scenes.Add(new Scene
                {
                    DeveloperId = developerId,
                    SceneId = sceneId,
                    LatestHash = sceneHash!,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                });
            }
            else
            {
                scene.LatestHash = sceneHash!;
                scene.UpdatedAtUtc = now;
            }

            db.SceneRevisions.Add(new SceneRevision
            {
                DeveloperId = developerId,
                SceneId = sceneId,
                Hash = sceneHash!,
                Version = version,
                SkippedMeshCount = skippedMeshes,
                RawGlbPath = rawRelative,
                RawSizeBytes = written,
                Status = SceneProcessingStatus.Pending,
                CreatedAtUtc = now,
            });

            // Sessions recorded before this version arrived can now compile.
            List<Session> waiting = await db.Sessions
                .Where(s => s.DeveloperId == developerId && s.SceneId == sceneId && s.SceneHash == sceneHash &&
                            s.Status == SessionStatus.WaitingForScene)
                .ToListAsync();
            foreach (Session s in waiting)
            {
                s.Status = SessionStatus.Queued;
            }

            try
            {
                await db.SaveChangesAsync();
                signal.Signal.Notify();
                ConsoleLog.Info(
                    $"Scene upload: developer={developerId} scene={sceneId} v{version} hash={sceneHash![..8]} bytes={written}" +
                    (skippedMeshes > 0 ? $" skippedMeshes={skippedMeshes}" : "") +
                    (waiting.Count > 0 ? $", {waiting.Count} waiting session(s) queued" : ""));
                return Results.Ok();
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                if (await RevisionExistsAsync(db, developerId, sceneId, sceneHash!))
                {
                    // A concurrent upload of the same hash won; it keeps its own file.
                    TryDelete(rawFull);
                    return Results.Ok();
                }

                // Lost the race for the version number; try the next one.
            }
        }

        TryDelete(rawFull);
        return Results.Problem("Could not store the scene version; try again.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static Task<bool> RevisionExistsAsync(MoodyBluesDbContext db, string developerId, string sceneId, string hash) =>
        db.SceneRevisions.AnyAsync(r => r.DeveloperId == developerId && r.SceneId == sceneId && r.Hash == hash);

    private static void TryDelete(string fullPath)
    {
        try
        {
            File.Delete(fullPath);
        }
        catch (IOException)
        {
            // Left behind; harmless, it is not referenced by any row.
        }
    }
}
