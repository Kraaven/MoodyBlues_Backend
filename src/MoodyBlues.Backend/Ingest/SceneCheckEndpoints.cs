using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Logging;

namespace MoodyBlues.Backend.Ingest;

/// <summary>
/// <c>POST /scenes/check</c> (Unity Spec 13): asks whether this scene revision is already stored,
/// without starting a session. Used by the client's SendScene so a scene can be uploaded ahead of
/// recording, and only when the stored copy is missing.
/// </summary>
public static class SceneCheckEndpoints
{
    public static void Map(WebApplication app)
    {
        // A literal segment, so routing prefers it over POST /scenes/{sceneId} (the upload).
        app.MapPost("/scenes/check", HandleAsync).RequireRateLimiting(RateLimits.Ingest);
    }

    /// <summary>Public so tests can call it directly against an in-memory database.</summary>
    public static async Task<IResult> HandleAsync(SceneCheckRequest request, MoodyBluesDbContext db, ServerConfig config)
    {
        if (string.IsNullOrWhiteSpace(request.DeveloperId) ||
            string.IsNullOrWhiteSpace(request.SceneId) ||
            string.IsNullOrWhiteSpace(request.SceneHash))
        {
            return Results.BadRequest("developerId, sceneId and sceneHash are all required.");
        }

        if (!PathSegments.IsSafe(request.DeveloperId) || !PathSegments.IsSafe(request.SceneId))
        {
            return Results.BadRequest("developerId and sceneId may only contain letters, digits, '-' and '_'.");
        }

        if (!PathSegments.IsSceneHash(request.SceneHash))
        {
            return Results.BadRequest("sceneHash must be 64 lowercase hex characters.");
        }

        if (!await IngestAccess.IsRegisteredDeveloperAsync(db, request.DeveloperId))
        {
            return Results.NotFound(IngestAccess.UnknownDeveloperMessage);
        }

        bool sceneUploadRequired = !await db.SceneRevisions.AnyAsync(r =>
            r.DeveloperId == request.DeveloperId && r.SceneId == request.SceneId && r.Hash == request.SceneHash);

        string? sceneUploadUrl = sceneUploadRequired
            ? BuildSceneUploadUrl(config, request.DeveloperId, request.SceneId, request.SceneHash)
            : null;

        ConsoleLog.Info(
            $"Scene check: developer={request.DeveloperId} scene={request.SceneId} sceneUploadRequired={sceneUploadRequired}");

        return Results.Ok(new SceneCheckResponse(sceneUploadRequired, sceneUploadUrl));
    }

    /// <summary>The upload URL handed to the client, shared with the handshake.</summary>
    public static string BuildSceneUploadUrl(ServerConfig config, string developerId, string sceneId, string sceneHash) =>
        $"{(config.IsHttps ? "https" : "http")}://{config.PublicHostAndPort()}/scenes/{sceneId}" +
        $"?developerId={developerId}&sceneHash={sceneHash}";
}
