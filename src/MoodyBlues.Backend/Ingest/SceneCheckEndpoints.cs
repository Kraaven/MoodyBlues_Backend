using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
        if (IngestScenes.Validate(request.SceneId, request.SceneHash) is { } invalid)
        {
            return invalid;
        }

        (IngestProject? project, IResult? denied) = await IngestAccess.ResolveAsync(db, httpRequest);
        if (denied is not null)
        {
            return denied;
        }

        (bool sceneUploadRequired, string? sceneUploadUrl) =
            await IngestScenes.CheckAsync(db, config, project!.ProjectId, request.SceneId, request.SceneHash);

        ConsoleLog.Info(
            $"Scene check: project={project.ProjectId} scene={request.SceneId} sceneUploadRequired={sceneUploadRequired}");

        return Results.Ok(new SceneCheckResponse(sceneUploadRequired, sceneUploadUrl));
    }
}
