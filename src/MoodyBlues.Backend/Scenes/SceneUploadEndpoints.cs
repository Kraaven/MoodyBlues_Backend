using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Logging;

namespace MoodyBlues.Backend.Scenes;

/// <summary>
/// <c>POST /scenes/{sceneId}</c> -- receives the exported <c>.glb</c> for a
/// scene that <c>/handshake</c> flagged as out of date.
///
/// This milestone does no GLTF parsing at all: the bytes are written to disk
/// as-is ("processed later") and the scene's hash is updated so the *next*
/// handshake sees it as up to date. Building a server-side object registry
/// from the file's <c>node.extras.objectId</c> values is a later milestone.
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
        ServerConfig config)
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

        await UpsertSceneAsync(db, developerId, sceneId, sceneHash, relativePath);

        ConsoleLog.Info($"Scene upload: developer={developerId} scene={sceneId} bytes={bytesWritten} -> {fullPath}");

        return Results.Ok();
    }

    private static async Task UpsertSceneAsync(MoodyBluesDbContext db, string developerId, string sceneId, string sceneHash, string relativePath)
    {
        Data.Scene? scene = await db.Scenes.FindAsync(developerId, sceneId);
        if (scene is null)
        {
            db.Scenes.Add(new Data.Scene
            {
                DeveloperId = developerId,
                SceneId = sceneId,
                Hash = sceneHash,
                GlbPath = relativePath,
                UpdatedAtUtc = DateTime.UtcNow,
            });
        }
        else
        {
            scene.Hash = sceneHash;
            scene.GlbPath = relativePath;
            scene.UpdatedAtUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
    }
}
