using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Logging;
using MoodyBlues.Backend.Scenes.Processing;

namespace MoodyBlues.Backend.Scenes;

/// <summary>
/// <c>POST /scenes/{sceneId}</c> -- receives the exported <c>.glb</c> for a
/// scene that <c>/handshake</c> flagged as out of date.
///
/// The raw bytes are written to disk as-is and the scene's hash is updated so the *next*
/// handshake sees it as up to date -- this must stay fast and never block Unity's upload.
/// A <see cref="SceneProcessingJob"/> is enqueued so a background worker
/// (<see cref="SceneProcessingWorker"/>) can Draco/KTX2-optimize the file asynchronously; see
/// <see cref="SceneEndpoints.DownloadAsync"/> for how the dashboard picks between the raw and
/// optimized file. Building a server-side object registry from the file's
/// <c>node.extras.objectId</c> values is a later milestone.
/// </summary>
public static class SceneUploadEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/scenes/{sceneId}", HandleAsync);
    }

    /// <summary>Public (rather than private) so it can be exercised directly in tests without a live Postgres.</summary>
    public static async Task<IResult> HandleAsync(
        string sceneId,
        HttpRequest request,
        MoodyBluesDbContext db,
        ServerConfig config,
        SceneProcessingQueue processingQueue)
    {
        string? developerId = request.Query["developerId"];
        string? sceneHash = request.Query["sceneHash"];

        if (string.IsNullOrWhiteSpace(developerId) || string.IsNullOrWhiteSpace(sceneHash))
        {
            return Results.BadRequest("developerId and sceneHash query parameters are required.");
        }

        if (!PathSegments.IsSafe(developerId) || !PathSegments.IsSafe(sceneId))
        {
            return Results.BadRequest("developerId and sceneId must not contain path separators.");
        }

        string developerDir = Path.Combine(config.ScenesDir, developerId);
        Directory.CreateDirectory(developerDir);

        string relativePath = Path.Combine(developerId, $"{sceneId}.glb");
        string fullPath = Path.Combine(config.ScenesDir, relativePath);

        long bytesWritten;
        await using (var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await request.Body.CopyToAsync(fileStream);
            bytesWritten = fileStream.Length;
        }

        await UpsertSceneAsync(db, developerId, sceneId, sceneHash, relativePath, bytesWritten);

        // Fire-and-forget: the worker picks this up on its own background loop. Never block the
        // Unity-facing upload response on optimization -- the raw file is already servable as-is.
        processingQueue.Enqueue(developerId, sceneId);

        ConsoleLog.Info($"Scene upload: developer={developerId} scene={sceneId} bytes={bytesWritten} -> {fullPath}");

        return Results.Ok();
    }

    private static async Task UpsertSceneAsync(MoodyBluesDbContext db, string developerId, string sceneId, string sceneHash, string relativePath, long rawSizeBytes)
    {
        Data.Scene? scene = await db.Scenes.FindAsync(developerId, sceneId);
        if (scene is null)
        {
            db.Scenes.Add(new Data.Scene
            {
                DeveloperId = developerId,
                SceneId = sceneId,
                Hash = sceneHash,
                RawGlbPath = relativePath,
                RawSizeBytes = rawSizeBytes,
                ProcessingStatus = SceneProcessingStatus.Pending,
                OptimizedGlbPath = null,
                OptimizedSizeBytes = null,
                UpdatedAtUtc = DateTime.UtcNow,
            });
        }
        else
        {
            scene.Hash = sceneHash;
            scene.RawGlbPath = relativePath;
            scene.RawSizeBytes = rawSizeBytes;
            scene.ProcessingStatus = SceneProcessingStatus.Pending;
            scene.OptimizedGlbPath = null;
            scene.OptimizedSizeBytes = null;
            scene.UpdatedAtUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
    }
}
