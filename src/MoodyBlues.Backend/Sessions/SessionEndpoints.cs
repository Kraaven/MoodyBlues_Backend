using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MoodyBlues.Backend.Auth;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Storage;

namespace MoodyBlues.Backend.Sessions;

/// <summary>
/// Dashboard session operations. Every route needs a JWT and checks the caller owns the project
/// the session's developerId belongs to (404 otherwise).
/// </summary>
public static class SessionEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGroup("/api/scenes/{developerId}/{sceneId}")
            .RequireAuthorization()
            .MapGet("/sessions", ListAsync);

        var group = app.MapGroup("/api/sessions/{sessionId}").RequireAuthorization();
        group.MapGet("/", GetAsync);
        group.MapGet("/replay", DownloadReplayAsync);
        group.MapPost("/recompile", RecompileAsync);
        group.MapDelete("/", DeleteAsync);
    }

    public static async Task<IResult> ListAsync(
        string developerId,
        string sceneId,
        int? version,
        bool? awaitingScene,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db)
    {
        if (!CurrentUser.TryGetUserId(principal, out Guid userId))
        {
            return Results.Unauthorized();
        }

        if (!PathSegments.IsSafe(developerId) || !PathSegments.IsSafe(sceneId))
        {
            return Results.BadRequest("Invalid developerId or sceneId.");
        }

        if (!await db.Projects.AnyAsync(p => p.DeveloperId == developerId && p.UserId == userId))
        {
            return Results.NotFound();
        }

        IQueryable<Session> query = db.Sessions.Where(s => s.DeveloperId == developerId && s.SceneId == sceneId);

        if (version is { } v)
        {
            // ?version=N: sessions recorded in that version (the current one, or one in the archive).
            string? hash = await db.SceneRevisions
                .Where(r => r.DeveloperId == developerId && r.SceneId == sceneId && r.Version == v)
                .Select(r => r.Hash)
                .FirstOrDefaultAsync();
            if (hash is null)
            {
                return Results.NotFound("That version does not exist.");
            }

            query = query.Where(s => s.SceneHash == hash);
        }
        else if (awaitingScene == true)
        {
            // ?awaitingScene=true: sessions recorded in a version that has not been uploaded yet.
            query = query.Where(s => !db.SceneRevisions.Any(r => r.DeveloperId == s.DeveloperId && r.SceneId == s.SceneId && r.Hash == s.SceneHash));
        }

        List<SessionSummaryResponse> sessions = await query
            .OrderByDescending(s => s.CreatedAtUtc)
            .Select(s => new SessionSummaryResponse(
                s.Id, s.SceneId, s.SceneHash, s.CreatedAtUtc, s.Status, s.EndedCleanly, s.DurationSeconds,
                s.ObjectCount, s.SpawnCount, s.KeyframeCount, s.ReplaySizeBytes, s.RawLogSizeBytes,
                db.SceneRevisions
                    .Where(r => r.DeveloperId == s.DeveloperId && r.SceneId == s.SceneId && r.Hash == s.SceneHash)
                    .Select(r => (int?)r.Version)
                    .FirstOrDefault(),
                s.ErrorMessage,
                s.InfoLogCount,
                s.WarningLogCount,
                s.ErrorLogCount))
            .ToListAsync();

        return Results.Ok(sessions);
    }

    public static async Task<IResult> GetAsync(string sessionId, ClaimsPrincipal principal, MoodyBluesDbContext db)
    {
        (IResult? error, Session? s) = await CheckOwnershipAsync(sessionId, principal, db);
        if (error is not null)
        {
            return error;
        }

        return Results.Ok(new SessionDetailResponse(
            s!.Id, s.DeveloperId, s.SceneId, s.SceneHash, s.CreatedAtUtc, s.LastActivityUtc, s.Status, s.EndedCleanly,
            s.ConnectionCount, s.DurationSeconds, s.ObjectCount, s.SpawnCount, s.RawEventCount, s.KeyframeCount,
            s.ReplaySizeBytes, s.RawLogSizeBytes, s.DeltaPromotions, s.HardClamps, s.DroppedEvents,
            s.CompilerVersion, s.CompiledAtUtc, s.ErrorMessage, s.InfoLogCount, s.WarningLogCount, s.ErrorLogCount));
    }

    /// <summary>
    /// The compiled replay file. Stored gzipped and sent with <c>Content-Encoding: gzip</c>, so the
    /// browser hands the viewer the uncompressed bytes.
    /// </summary>
    public static async Task<IResult> DownloadReplayAsync(
        string sessionId,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db,
        StoragePaths paths,
        HttpContext httpContext)
    {
        (IResult? error, Session? session) = await CheckOwnershipAsync(sessionId, principal, db);
        if (error is not null)
        {
            return error;
        }

        if (session!.Status != SessionStatus.Ready || session.ReplayPath is null)
        {
            return Results.NotFound("The replay is not ready yet.");
        }

        string fullPath = paths.SessionFull(session.ReplayPath);
        if (!File.Exists(fullPath))
        {
            return Results.NotFound("The replay file is not on disk.");
        }

        httpContext.Response.Headers.ContentEncoding = "gzip";
        httpContext.Response.Headers.CacheControl = "private, no-cache";
        return Results.File(fullPath, "application/octet-stream", $"{session.Id:N}.mbr");
    }

    /// <summary>Rebuilds the replay from the raw log, e.g. after a compiler improvement.</summary>
    public static async Task<IResult> RecompileAsync(
        string sessionId,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db,
        CompileWorkSignal signal)
    {
        (IResult? error, Session? session) = await CheckOwnershipAsync(sessionId, principal, db);
        if (error is not null)
        {
            return error;
        }

        if (session!.Status is SessionStatus.Recording or SessionStatus.Compiling or SessionStatus.Queued or SessionStatus.WaitingForScene)
        {
            return Results.Conflict("The session is still recording, waiting for its scene, or already waiting to compile.");
        }

        session.Status = SessionStatus.Queued;
        session.ErrorMessage = null;
        await db.SaveChangesAsync();
        signal.Signal.Notify();
        return Results.Accepted();
    }

    public static async Task<IResult> DeleteAsync(
        string sessionId,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db,
        StoragePaths paths)
    {
        (IResult? error, Session? session) = await CheckOwnershipAsync(sessionId, principal, db);
        if (error is not null)
        {
            return error;
        }

        if (session!.Status is SessionStatus.Recording or SessionStatus.Compiling)
        {
            return Results.Conflict("The session is still recording or compiling.");
        }

        db.Sessions.Remove(session);
        AuditLog.Add(
            db,
            CurrentUser.TryGetUserId(principal, out Guid userId) ? userId : null,
            AuditLog.SessionDelete,
            session.Id.ToString(),
            $"{session.DeveloperId}/{session.SceneId}, recorded {session.CreatedAtUtc:u}, {session.RawLogSizeBytes + session.ReplaySizeBytes} bytes");
        await db.SaveChangesAsync();

        // Files after the row, as in SceneDeletion: a failure leaves an orphaned folder, never a row without files.
        Scenes.SceneDeletion.TryDeleteDirectory(paths.SessionFull(StoragePaths.SessionDir(session.DeveloperId, session.Id)));
        return Results.NoContent();
    }

    private static async Task<(IResult? Error, Session? Session)> CheckOwnershipAsync(
        string sessionId,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db)
    {
        if (!CurrentUser.TryGetUserId(principal, out Guid userId))
        {
            return (Results.Unauthorized(), null);
        }

        if (!Guid.TryParse(sessionId, out Guid id))
        {
            return (Results.NotFound(), null);
        }

        Session? session = await db.Sessions.FindAsync(id);
        if (session is null || !await db.Projects.AnyAsync(p => p.DeveloperId == session.DeveloperId && p.UserId == userId))
        {
            return (Results.NotFound(), null);
        }

        return (null, session);
    }
}
