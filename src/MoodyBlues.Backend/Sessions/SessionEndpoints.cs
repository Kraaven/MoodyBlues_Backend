using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using MoodyBlues.Backend.Auth;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;

namespace MoodyBlues.Backend.Sessions;

/// <summary>
/// Dashboard-facing session (recorded-playback) operations. Every route requires a valid JWT and
/// checks that the caller owns the <see cref="Data.Project"/> that the session's
/// <c>developerId</c> belongs to -- same ownership pattern as <see cref="Scenes.SceneEndpoints"/>.
/// </summary>
public static class SessionEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGroup("/api/scenes/{developerId}/{sceneId}")
            .RequireAuthorization()
            .MapGet("/sessions", ListAsync);

        var sessionGroup = app.MapGroup("/api/sessions/{sessionId}").RequireAuthorization();
        sessionGroup.MapGet("/", GetAsync);
        sessionGroup.MapGet("/animation", DownloadAnimationAsync);
        sessionGroup.MapGet("/events", DownloadEventsAsync);
        sessionGroup.MapDelete("/", DeleteAsync);
    }

    /// <summary>Public so it can be exercised directly in tests without a live Postgres.</summary>
    public static async Task<IResult> ListAsync(
        string developerId,
        string sceneId,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db)
    {
        if (!CurrentUser.TryGetUserId(principal, out Guid userId))
        {
            return Results.Unauthorized();
        }

        if (!PathSegments.IsSafe(developerId) || !PathSegments.IsSafe(sceneId))
        {
            return Results.BadRequest("developerId and sceneId must not contain path separators.");
        }

        bool owns = await db.Projects.AnyAsync(p => p.DeveloperId == developerId && p.UserId == userId);
        if (!owns)
        {
            return Results.NotFound();
        }

        var sessions = await db.Sessions
            .Where(s => s.DeveloperId == developerId && s.SceneId == sceneId)
            .OrderByDescending(s => s.CreatedAtUtc)
            .Select(ToSummary)
            .ToListAsync();

        return Results.Ok(sessions);
    }

    /// <summary>Public so it can be exercised directly in tests without a live Postgres.</summary>
    public static async Task<IResult> GetAsync(string sessionId, ClaimsPrincipal principal, MoodyBluesDbContext db)
    {
        (IResult? error, Data.Session? session) = await CheckOwnershipAsync(sessionId, principal, db);
        if (error is not null)
        {
            return error;
        }

        return Results.Ok(new SessionDetailResponse(
            session!.Id,
            session.DeveloperId,
            session.SceneId,
            session.SceneHash,
            session.CreatedAtUtc,
            session.DurationSeconds,
            session.RawEventCount,
            session.KeyframeCount,
            session.ObjectCount,
            session.AnimationSizeBytes,
            session.EventLogSizeBytes,
            session.Status,
            session.ErrorMessage));
    }

    /// <summary>Public so it can be exercised directly in tests without a live Postgres.</summary>
    public static async Task<IResult> DownloadAnimationAsync(
        string sessionId,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db,
        ServerConfig config,
        HttpContext httpContext)
    {
        (IResult? error, Data.Session? session) = await CheckOwnershipAsync(sessionId, principal, db);
        if (error is not null)
        {
            return error;
        }

        if (session!.Status != SessionStatus.Ready || session.AnimationPath is null)
        {
            return Results.NotFound("Animation is not ready yet.");
        }

        string fullPath = Path.Combine(config.SessionsDir, session.AnimationPath);
        if (!File.Exists(fullPath))
        {
            return Results.NotFound("Animation file is not on disk.");
        }

        // The file on disk is gzipped JSON; setting Content-Encoding lets fetch()/the browser
        // transparently decompress it, same as any other gzip-compressed HTTP response.
        httpContext.Response.Headers.ContentEncoding = "gzip";

        // See SceneEndpoints.DownloadAsync for why this must be an absolute path (Results.File
        // otherwise resolves relative paths against a nonexistent wwwroot).
        return Results.File(Path.GetFullPath(fullPath), contentType: "application/json", fileDownloadName: $"{session.Id}.json");
    }

    /// <summary>Public so it can be exercised directly in tests without a live Postgres.</summary>
    public static async Task<IResult> DownloadEventsAsync(
        string sessionId,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db,
        ServerConfig config,
        HttpContext httpContext)
    {
        (IResult? error, Data.Session? session) = await CheckOwnershipAsync(sessionId, principal, db);
        if (error is not null)
        {
            return error;
        }

        if (session!.Status != SessionStatus.Ready || session.EventLogPath is null)
        {
            return Results.NotFound("Event log is not ready yet.");
        }

        string fullPath = Path.Combine(config.SessionsDir, session.EventLogPath);
        if (!File.Exists(fullPath))
        {
            return Results.NotFound("Event log file is not on disk.");
        }

        // The file on disk is gzipped binary; Content-Encoding lets fetch() decompress transparently.
        httpContext.Response.Headers.ContentEncoding = "gzip";

        return Results.File(
            Path.GetFullPath(fullPath),
            contentType: "application/octet-stream",
            fileDownloadName: $"{session.Id}.events.bin");
    }

    /// <summary>Public so it can be exercised directly in tests without a live Postgres.</summary>
    public static async Task<IResult> DeleteAsync(
        string sessionId,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db,
        ServerConfig config)
    {
        (IResult? error, Data.Session? session) = await CheckOwnershipAsync(sessionId, principal, db);
        if (error is not null)
        {
            return error;
        }

        if (session!.AnimationPath is not null)
        {
            string fullPath = Path.Combine(config.SessionsDir, session.AnimationPath);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }

        if (session.EventLogPath is not null)
        {
            string eventsPath = Path.Combine(config.SessionsDir, session.EventLogPath);
            if (File.Exists(eventsPath))
            {
                File.Delete(eventsPath);
            }
        }

        db.Sessions.Remove(session);
        await db.SaveChangesAsync();

        return Results.NoContent();
    }

    private static async Task<(IResult? Error, Data.Session? Session)> CheckOwnershipAsync(
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

        Data.Session? session = await db.Sessions.FindAsync(id);
        if (session is null)
        {
            return (Results.NotFound(), null);
        }

        bool owns = await db.Projects.AnyAsync(p => p.DeveloperId == session.DeveloperId && p.UserId == userId);
        if (!owns)
        {
            return (Results.NotFound(), null);
        }

        return (null, session);
    }

    private static readonly System.Linq.Expressions.Expression<Func<Data.Session, SessionSummaryResponse>> ToSummary =
        s => new SessionSummaryResponse(
            s.Id,
            s.SceneId,
            s.CreatedAtUtc,
            s.DurationSeconds,
            s.RawEventCount,
            s.KeyframeCount,
            s.ObjectCount,
            s.AnimationSizeBytes,
            s.EventLogSizeBytes,
            s.Status);
}
