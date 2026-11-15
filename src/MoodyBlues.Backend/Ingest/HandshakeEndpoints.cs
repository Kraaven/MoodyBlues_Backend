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
        if (IngestScenes.Validate(request.SceneId, request.SceneHash) is { } invalid)
        {
            return invalid;
        }

        if (!PathSegments.IsSafe(request.SessionId))
        {
            return Results.BadRequest("sessionId is required and may only contain letters, digits, '-' and '_'.");
        }

        (IngestProject? project, IResult? denied) = await IngestAccess.ResolveAsync(db, httpRequest);
        if (denied is not null)
        {
            return denied;
        }

        Guid projectId = project!.ProjectId;

        // A retried handshake (same client run) reuses the session; a fresh token is issued and
        // the old one stops working, so only the latest token the client was given is valid.
        string token = StreamTokens.NewToken();
        Session? session = await db.Sessions.FirstOrDefaultAsync(s =>
            s.ProjectId == projectId && s.ClientSessionId == request.SessionId);

        if (session is null)
        {
            if (config.MaxConcurrentSessionsPerProject > 0 &&
                await db.Sessions.CountAsync(s => s.ProjectId == projectId && s.Status == SessionStatus.Recording)
                    >= config.MaxConcurrentSessionsPerProject)
            {
                return Results.Problem(
                    $"This project already has {config.MaxConcurrentSessionsPerProject} recordings in progress.",
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            var id = Guid.NewGuid();
            DateTime now = DateTime.UtcNow;
            session = new Session
            {
                Id = id,
                ProjectId = projectId,
                SceneId = request.SceneId,
                SceneHash = request.SceneHash,
                ClientSessionId = request.SessionId,
                StreamTokenHash = Hashing.Sha256Hex(token),
                Status = SessionStatus.Recording,
                CreatedAtUtc = now,
                LastActivityUtc = now,
                RawLogPath = Path.Combine(StoragePaths.SessionDir(projectId, id), StoragePaths.RawLogFile),
            };
            db.Sessions.Add(session);
        }
        else if (session.Status != SessionStatus.Recording)
        {
            return Results.Conflict("This session has already ended. Start a new play session.");
        }
        else
        {
            session.StreamTokenHash = Hashing.Sha256Hex(token);
            session.LastActivityUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();

        (bool sceneUploadRequired, string? sceneUploadUrl) =
            await IngestScenes.CheckAsync(db, config, projectId, request.SceneId, request.SceneHash);

        // The stream token travels in a header (StreamServer.TokenHeader), not the URL, so it never
        // lands in proxy access logs.
        string hostAndPort = config.PublicHostAndPort();
        string webSocketUrl = $"{(config.IsHttps ? "wss" : "ws")}://{hostAndPort}/stream";
        ConsoleLog.Info(
            $"Handshake: project={projectId} scene={request.SceneId} session={session.Id} " +
            $"sceneUploadRequired={sceneUploadRequired}");

        return Results.Ok(new HandshakeResponse(webSocketUrl, token, sceneUploadRequired, sceneUploadUrl));
    }
}
