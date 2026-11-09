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

        // The Unity editor's "Test connection": which project does this key belong to?
        app.MapGet("/connection", ConnectionAsync).RequireRateLimiting(RateLimits.Ingest);
    }

    /// <summary>
    /// <c>GET /connection</c>: 200 with the project's name if the ingest key is valid, 401
    /// otherwise. Read-only and reveals nothing beyond the name of the key's own project.
    /// </summary>
    public static async Task<IResult> ConnectionAsync(HttpRequest httpRequest, MoodyBluesDbContext db)
    {
        (IngestProject? project, IResult? denied) = await IngestAccess.ResolveAsync(db, httpRequest);
        return denied ?? Results.Ok(new ConnectionResponse(project!.Name));
    }

    /// <summary>Public so tests can call it directly against an in-memory database.</summary>
    public static async Task<IResult> HandleAsync(SceneCheckRequest request, HttpRequest httpRequest, MoodyBluesDbContext db, ServerConfig config)
    {
        if (string.IsNullOrWhiteSpace(request.SceneId) || string.IsNullOrWhiteSpace(request.SceneHash))
        {
            return Results.BadRequest("sceneId and sceneHash are both required.");
        }

        if (!PathSegments.IsSafe(request.SceneId))
        {
            return Results.BadRequest("sceneId may only contain letters, digits, '-' and '_'.");
        }

        if (!PathSegments.IsSceneHash(request.SceneHash))
        {
            return Results.BadRequest("sceneHash must be 64 lowercase hex characters.");
        }

        (IngestProject? project, IResult? denied) = await IngestAccess.ResolveAsync(db, httpRequest);
        if (denied is not null)
        {
            return denied;
        }

        string developerId = project!.DeveloperId;
        bool sceneUploadRequired = !await db.SceneRevisions.AnyAsync(r =>
            r.DeveloperId == developerId && r.SceneId == request.SceneId && r.Hash == request.SceneHash);

        string? sceneUploadUrl = sceneUploadRequired
            ? BuildSceneUploadUrl(config, request.SceneId, request.SceneHash)
            : null;

        ConsoleLog.Info(
            $"Scene check: developer={developerId} scene={request.SceneId} sceneUploadRequired={sceneUploadRequired}");

        return Results.Ok(new SceneCheckResponse(sceneUploadRequired, sceneUploadUrl));
    }

    /// <summary>The upload URL handed to the client, shared with the handshake. The project comes from the key.</summary>
    public static string BuildSceneUploadUrl(ServerConfig config, string sceneId, string sceneHash) =>
        $"{(config.IsHttps ? "https" : "http")}://{config.PublicHostAndPort()}/scenes/{sceneId}?sceneHash={sceneHash}";
}
