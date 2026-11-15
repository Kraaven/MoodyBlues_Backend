using Microsoft.AspNetCore.Http;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Scenes;

namespace MoodyBlues.Backend.Ingest;

/// <summary>
/// What the handshake and <c>POST /scenes/check</c> have in common: validating the scene reference
/// a Unity client sends, and answering whether that scene version still has to be uploaded.
/// </summary>
public static class IngestScenes
{
    /// <summary>The 400 to return when the scene reference is unusable, or null when it is fine.</summary>
    public static IResult? Validate(string? sceneId, string? sceneHash)
    {
        if (string.IsNullOrWhiteSpace(sceneId) || string.IsNullOrWhiteSpace(sceneHash))
        {
            return Results.BadRequest("sceneId and sceneHash are both required.");
        }

        if (!PathSegments.IsSafe(sceneId))
        {
            return Results.BadRequest("sceneId may only contain letters, digits, '-' and '_'.");
        }

        if (!PathSegments.IsSceneHash(sceneHash))
        {
            return Results.BadRequest("sceneHash must be 64 lowercase hex characters.");
        }

        return null;
    }

    /// <summary>Whether the version must be uploaded, and the URL to upload it to when it must.</summary>
    public static async Task<(bool UploadRequired, string? UploadUrl)> CheckAsync(
        MoodyBluesDbContext db, ServerConfig config, Guid projectId, string sceneId, string sceneHash)
    {
        bool required = !await SceneRevisions.ExistsAsync(db, projectId, sceneId, sceneHash);
        return (required, required ? BuildUploadUrl(config, sceneId, sceneHash) : null);
    }

    /// <summary>The upload URL handed to the client. The project comes from the ingest key, so it is not in the URL.</summary>
    public static string BuildUploadUrl(ServerConfig config, string sceneId, string sceneHash) =>
        $"{(config.IsHttps ? "https" : "http")}://{config.PublicHostAndPort()}/scenes/{sceneId}?sceneHash={sceneHash}";
}
