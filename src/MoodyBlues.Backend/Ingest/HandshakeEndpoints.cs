using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Logging;
using MoodyBlues.Backend.Storage;

namespace MoodyBlues.Backend.Ingest;

/// <summary>
/// <c>POST /handshake</c>: a Unity client starting a play session. Creates (or, for a retried
/// handshake with the same client session id, reuses) the <see cref="Session"/> row, and tells
/// the client where to stream and whether this scene revision still needs uploading.
/// </summary>
public static class HandshakeEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/handshake", HandleAsync).RequireRateLimiting(RateLimits.Ingest);
    }

    /// <summary>Public so tests can call it directly against an in-memory database.</summary>
    public static async Task<IResult> HandleAsync(HandshakeRequest request, MoodyBluesDbContext db, ServerConfig config)
    {
        if (string.IsNullOrWhiteSpace(request.DeveloperId) ||
            string.IsNullOrWhiteSpace(request.SceneId) ||
            string.IsNullOrWhiteSpace(request.SceneHash) ||
            string.IsNullOrWhiteSpace(request.SessionId))
        {
            return Results.BadRequest("developerId, sceneId, sceneHash, and sessionId are all required.");
        }

        if (!PathSegments.IsSafe(request.DeveloperId) || !PathSegments.IsSafe(request.SceneId) || !PathSegments.IsSafe(request.SessionId))
        {
            return Results.BadRequest("developerId, sceneId and sessionId may only contain letters, digits, '-' and '_'.");
        }

        if (!PathSegments.IsSceneHash(request.SceneHash))
        {
            return Results.BadRequest("sceneHash must be 64 lowercase hex characters.");
        }

        await DeveloperProvisioning.EnsureExistsAsync(db, request.DeveloperId);

        // A retried handshake (same client run) reuses the session; a fresh token is issued and
        // the old one stops working, so only the latest URL the client was given is valid.
        string token = StreamTokens.NewToken();
        Session? session = await db.Sessions.FirstOrDefaultAsync(s =>
            s.DeveloperId == request.DeveloperId && s.ClientSessionId == request.SessionId);

        if (session is null)
        {
            var id = Guid.NewGuid();
            DateTime now = DateTime.UtcNow;
            session = new Session
            {
                Id = id,
                DeveloperId = request.DeveloperId,
                SceneId = request.SceneId,
                SceneHash = request.SceneHash,
                ClientSessionId = request.SessionId,
                StreamTokenHash = StreamTokens.Hash(token),
                Status = SessionStatus.Recording,
                CreatedAtUtc = now,
                LastActivityUtc = now,
                RawLogPath = Path.Combine(StoragePaths.SessionDir(request.DeveloperId, id), StoragePaths.RawLogFile),
            };
            db.Sessions.Add(session);
        }
        else if (session.Status != SessionStatus.Recording)
        {
            return Results.Conflict("This session has already ended. Start a new play session.");
        }
        else
        {
            session.StreamTokenHash = StreamTokens.Hash(token);
            session.LastActivityUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();

        bool sceneUploadRequired = !await db.SceneRevisions.AnyAsync(r =>
            r.DeveloperId == request.DeveloperId && r.SceneId == request.SceneId && r.Hash == request.SceneHash);

        string hostAndPort = config.PublicHostAndPort();
        string webSocketUrl = $"{(config.IsHttps ? "wss" : "ws")}://{hostAndPort}/stream?token={token}";
        string? sceneUploadUrl = sceneUploadRequired
            ? SceneCheckEndpoints.BuildSceneUploadUrl(config, request.DeveloperId, request.SceneId, request.SceneHash)
            : null;

        ConsoleLog.Info(
            $"Handshake: developer={request.DeveloperId} scene={request.SceneId} session={session.Id} " +
            $"sceneUploadRequired={sceneUploadRequired}");

        return Results.Ok(new HandshakeResponse(webSocketUrl, sceneUploadRequired, sceneUploadUrl));
    }
}
