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
/// <c>POST /scenes/{sceneId}?developerId=&amp;sceneHash=</c>: the Unity client uploading the GLB
/// of a scene revision the handshake said was missing (Unity Spec 13.3). The body is the .glb,
/// usually gzip-encoded.
///
/// Revisions are immutable: if this hash is already stored, the upload is accepted and ignored.
/// The file is written to a temporary path and moved into place only once complete and checked
/// to start with the GLB magic, so a broken upload never replaces anything.
/// </summary>
public static class SceneUploadEndpoints
{
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
        string? developerId = request.Query["developerId"];
        string? sceneHash = request.Query["sceneHash"];

        if (!PathSegments.IsSafe(developerId) || !PathSegments.IsSafe(sceneId) || !PathSegments.IsSceneHash(sceneHash))
        {
            return Results.BadRequest("developerId, sceneId and a 64-character lowercase hex sceneHash are required.");
        }

        if (!await db.Developers.AnyAsync(d => d.Id == developerId))
        {
            return Results.NotFound("Unknown developerId; the client must handshake first.");
        }

        if (await db.SceneRevisions.AnyAsync(r => r.DeveloperId == developerId && r.SceneId == sceneId && r.Hash == sceneHash))
        {
            return Results.Ok(); // already have this revision; revisions never change
        }

        // The size cap is enforced on the decompressed stream we actually read, which is what
        // protects the disk from a gzip bomb. Kestrel's own limit is lifted for this request.
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
        {
            sizeFeature.MaxRequestBodySize = null;
        }

        string dir = StoragePaths.SceneRevisionDir(developerId!, sceneId, sceneHash!);
        string rawRelative = Path.Combine(dir, StoragePaths.RawGlbFile);
        string rawFull = paths.SceneFull(rawRelative);

        long written = 0;
        bool tooLarge = false, notGlb = false;
        try
        {
            await StoragePaths.WriteAtomicAsync(rawFull, async output =>
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await request.Body.ReadAsync(buffer)) > 0)
                {
                    if (written == 0 && (read < 4 || buffer[0] != (byte)'g' || buffer[1] != (byte)'l' || buffer[2] != (byte)'T' || buffer[3] != (byte)'F'))
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

                if (written == 0)
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

        DateTime now = DateTime.UtcNow;
        Scene? scene = await db.Scenes.FindAsync(developerId, sceneId);
        if (scene is null)
        {
            db.Scenes.Add(new Scene
            {
                DeveloperId = developerId!,
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
            DeveloperId = developerId!,
            SceneId = sceneId,
            Hash = sceneHash!,
            RawGlbPath = rawRelative,
            RawSizeBytes = written,
            Status = SceneProcessingStatus.Pending,
            CreatedAtUtc = now,
        });

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // A concurrent upload of the same revision won; its row and file are equivalent.
            return Results.Ok();
        }

        signal.Signal.Notify();
        ConsoleLog.Info($"Scene upload: developer={developerId} scene={sceneId} hash={sceneHash![..8]} bytes={written}");
        return Results.Ok();
    }
}
