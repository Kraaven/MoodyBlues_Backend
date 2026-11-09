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
    public static async Task<IResult> HandleAsync(HandshakeRequest request, HttpRequest httpRequest, MoodyBluesDbContext db, ServerConfig config)
    {
        if (string.IsNullOrWhiteSpace(request.SceneId) ||
            string.IsNullOrWhiteSpace(request.SceneHash) ||
            string.IsNullOrWhiteSpace(request.SessionId))
        {
            return Results.BadRequest("sceneId, sceneHash and sessionId are all required.");
        }

        if (!PathSegments.IsSafe(request.SceneId) || !PathSegments.IsSafe(request.SessionId))
        {
            return Results.BadRequest("sceneId and sessionId may only contain letters, digits, '-' and '_'.");
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

        // A retried handshake (same client run) reuses the session; a fresh token is issued and
        // the old one stops working, so only the latest token the client was given is valid.
        string token = StreamTokens.NewToken();
        Session? session = await db.Sessions.FirstOrDefaultAsync(s =>
            s.DeveloperId == developerId && s.ClientSessionId == request.SessionId);

        if (session is null)
        {
            if (config.MaxConcurrentSessionsPerDeveloper > 0 &&
                await db.Sessions.CountAsync(s => s.DeveloperId == developerId && s.Status == SessionStatus.Recording)
                    >= config.MaxConcurrentSessionsPerDeveloper)
            {
                return Results.Problem(
                    $"This project already has {config.MaxConcurrentSessionsPerDeveloper} recordings in progress.",
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            var id = Guid.NewGuid();
            DateTime now = DateTime.UtcNow;
            session = new Session
            {
                Id = id,
                DeveloperId = developerId,
                SceneId = request.SceneId,
                SceneHash = request.SceneHash,
                ClientSessionId = request.SessionId,
                StreamTokenHash = StreamTokens.Hash(token),
                Status = SessionStatus.Recording,
                CreatedAtUtc = now,
                LastActivityUtc = now,
                RawLogPath = Path.Combine(StoragePaths.SessionDir(developerId, id), StoragePaths.RawLogFile),
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
            r.DeveloperId == developerId && r.SceneId == request.SceneId && r.Hash == request.SceneHash);

        // The stream token travels in a header (StreamServer.TokenHeader), not the URL, so it never
        // lands in proxy access logs.
        string hostAndPort = config.PublicHostAndPort();
        string webSocketUrl = $"{(config.IsHttps ? "wss" : "ws")}://{hostAndPort}/stream";
        string? sceneUploadUrl = sceneUploadRequired
            ? SceneCheckEndpoints.BuildSceneUploadUrl(config, request.SceneId, request.SceneHash)
            : null;

        ConsoleLog.Info(
            $"Handshake: developer={developerId} scene={request.SceneId} session={session.Id} " +
            $"sceneUploadRequired={sceneUploadRequired}");

        return Results.Ok(new HandshakeResponse(webSocketUrl, token, sceneUploadRequired, sceneUploadUrl));
    }
}
