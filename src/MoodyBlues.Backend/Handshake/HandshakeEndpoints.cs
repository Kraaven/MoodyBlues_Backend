using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Logging;

namespace MoodyBlues.Backend.Handshake;

/// <summary>
/// <c>POST /handshake</c> -- the entry point for a Unity client starting up.
/// Tells it which WebSocket URL to stream events to, and whether it needs to
/// export/upload a fresh copy of the scene first (see Scenes/SceneUploadEndpoints.cs).
/// </summary>
public static class HandshakeEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/handshake", HandleAsync);
    }

    /// <summary>Public (rather than private) so it can be exercised directly in tests without a live Postgres.</summary>
    public static async Task<IResult> HandleAsync(
        HandshakeRequest request,
        MoodyBluesDbContext db,
        ServerConfig config,
        PendingSessionStore sessions)
    {
        if (string.IsNullOrWhiteSpace(request.DeveloperId) ||
            string.IsNullOrWhiteSpace(request.SceneId) ||
            string.IsNullOrWhiteSpace(request.SceneHash) ||
            string.IsNullOrWhiteSpace(request.SessionId))
        {
            return Results.BadRequest("developerId, sceneId, sceneHash, and sessionId are all required.");
        }

        if (!PathSegments.IsSafe(request.DeveloperId) || !PathSegments.IsSafe(request.SceneId))
        {
            return Results.BadRequest("developerId and sceneId must not contain path separators.");
        }

        await EnsureDeveloperExistsAsync(db, request.DeveloperId);

        Data.Scene? scene = await db.Scenes.FindAsync(request.DeveloperId, request.SceneId);
        bool sceneUploadRequired = scene is null || scene.Hash != request.SceneHash;

        // Single-use token the /stream WebSocket endpoint will redeem to learn which developer/scene this
        // connection belongs to -- the binary event wire protocol itself carries no such context.
        sessions.Put(request.SessionId, request.DeveloperId, request.SceneId);

        string webSocketUrl = $"ws://{config.PublicHost}:{config.Port}/stream?session={Uri.EscapeDataString(request.SessionId)}";
        string? sceneUploadUrl = sceneUploadRequired
            ? $"http://{config.PublicHost}:{config.Port}/scenes/{Uri.EscapeDataString(request.SceneId)}" +
              $"?developerId={Uri.EscapeDataString(request.DeveloperId)}&sceneHash={Uri.EscapeDataString(request.SceneHash)}"
            : null;

        ConsoleLog.Info(
            $"Handshake: developer={request.DeveloperId} scene={request.SceneId} session={request.SessionId} " +
            $"sceneUploadRequired={sceneUploadRequired}");

        return Results.Ok(new HandshakeResponse(webSocketUrl, sceneUploadRequired, sceneUploadUrl));
    }

    /// <summary>Auto-provisions a Developer row on first sight -- there is no registration/auth endpoint yet.</summary>
    private static async Task EnsureDeveloperExistsAsync(MoodyBluesDbContext db, string developerId)
    {
        bool exists = await db.Developers.AnyAsync(d => d.Id == developerId);
        if (exists)
        {
            return;
        }

        db.Developers.Add(new Data.Developer { Id = developerId, CreatedAtUtc = DateTime.UtcNow });
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Lost a race with a concurrent handshake auto-provisioning the same developer -- fine, it exists now.
        }
    }
}
