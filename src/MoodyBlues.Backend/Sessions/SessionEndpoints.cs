using System.Globalization;
using System.IO.Compression;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using MoodyBlues.Backend.Auth;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Compiler;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Projects;
using MoodyBlues.Backend.Scenes;
using MoodyBlues.Backend.Storage;

namespace MoodyBlues.Backend.Sessions;

/// <summary>
/// Dashboard session operations. Every route needs a JWT and checks the caller owns the session's
/// project (<see cref="ProjectAccess"/>; 404 otherwise).
/// </summary>
public static class SessionEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGroup("/api/projects/{projectId:guid}/scenes/{sceneId}")
            .RequireAuthorization()
            .MapGet("/sessions", ListAsync);

        var group = app.MapGroup("/api/sessions/{sessionId}").RequireAuthorization();
        group.MapGet("/", GetAsync);
        group.MapGet("/replay", DownloadReplayAsync);
        group.MapPut("/sharing", SetSharingAsync);
        group.MapPost("/sharing/reset", ResetSharingAsync);
        group.MapPost("/recompile", RecompileAsync);
        group.MapDelete("/", DeleteAsync);
    }

    public static async Task<IResult> ListAsync(
        Guid projectId,
        string sceneId,
        int? version,
        bool? awaitingScene,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db)
    {
        (IResult? error, Project? _) = await ProjectAccess.FindOwnedAsync(db, principal, projectId);
        if (error is not null)
        {
            return error;
        }

        if (!PathSegments.IsSafe(sceneId))
        {
            return Results.BadRequest("Invalid sceneId.");
        }

        IQueryable<Session> query = db.Sessions.Where(s => s.ProjectId == projectId && s.SceneId == sceneId);

        if (version is { } v)
        {
            // ?version=N: sessions recorded in that version (the current one, or one in the archive).
            string? hash = await db.SceneRevisions
                .Where(r => r.ProjectId == projectId && r.SceneId == sceneId && r.Version == v)
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
            query = query.Where(s => !db.SceneRevisions.Any(r => r.ProjectId == s.ProjectId && r.SceneId == s.SceneId && r.Hash == s.SceneHash));
        }

        // The activity is stored as JSON text; it is parsed after the query, not inside it.
        var rows = await query
            .OrderByDescending(s => s.CreatedAtUtc)
            .Select(s => new
            {
                Session = s,
                SceneVersion = db.SceneRevisions
                    .Where(r => r.ProjectId == s.ProjectId && r.SceneId == s.SceneId && r.Hash == s.SceneHash)
                    .Select(r => (int?)r.Version)
                    .FirstOrDefault(),
            })
            .ToListAsync();

        List<SessionSummaryResponse> sessions = rows
            .Select(x => new SessionSummaryResponse(
                x.Session.Id, x.Session.SceneId, x.Session.SceneHash, x.Session.CreatedAtUtc, x.Session.Status,
                x.Session.EndedCleanly, x.Session.DurationSeconds, x.Session.ObjectCount, x.Session.SpawnCount,
                x.Session.KeyframeCount, x.Session.ReplaySizeBytes, x.Session.RawLogSizeBytes, x.SceneVersion,
                x.Session.ErrorMessage, x.Session.InfoLogCount, x.Session.WarningLogCount, x.Session.ErrorLogCount,
                SessionActivity.FromJson(x.Session.ActivityJson), x.Session.IsPublic, x.Session.ShareToken))
            .ToList();

        return Results.Ok(sessions);
    }

    public static async Task<IResult> GetAsync(string sessionId, ClaimsPrincipal principal, MoodyBluesDbContext db)
    {
        (IResult? error, Session? s) = await CheckOwnershipAsync(sessionId, principal, db);
        if (error is not null)
        {
            return error;
        }

        return Results.Ok(ToDetail(s!));
    }

    internal static SessionDetailResponse ToDetail(Session s) => new(
        s.Id, s.ProjectId, s.SceneId, s.SceneHash, s.CreatedAtUtc, s.LastActivityUtc, s.Status, s.EndedCleanly,
        s.ConnectionCount, s.DurationSeconds, s.ObjectCount, s.SpawnCount, s.KeyframeCount,
        s.ReplaySizeBytes, s.RawLogSizeBytes, s.DeltaPromotions, s.HardClamps, s.DroppedEvents, s.RotationWidenings,
        s.CompilerVersion, s.CompiledAtUtc, s.ErrorMessage, s.InfoLogCount, s.WarningLogCount, s.ErrorLogCount,
        SessionActivity.FromJson(s.ActivityJson), s.IsPublic, s.ShareToken);

    /// <summary>Turns public sharing on or off. The token is created on first use and kept while private.</summary>
    public static async Task<IResult> SetSharingAsync(
        string sessionId,
        SharingRequest request,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db)
    {
        (IResult? error, Session? session) = await CheckOwnershipAsync(sessionId, principal, db);
        if (error is not null)
        {
            return error;
        }

        session!.IsPublic = request.IsPublic;
        if (request.IsPublic && session.ShareToken is null)
        {
            session.ShareToken = ShareTokens.Create();
        }

        AuditLog.Add(db, UserIdOf(principal), AuditLog.SessionShare, session.Id.ToString(), request.IsPublic ? "public" : "private");
        await db.SaveChangesAsync();
        return Results.Ok(new SharingResponse(session.IsPublic, session.ShareToken));
    }

    /// <summary>Issues a new share token (even while private), which invalidates the old link.</summary>
    public static async Task<IResult> ResetSharingAsync(string sessionId, ClaimsPrincipal principal, MoodyBluesDbContext db)
    {
        (IResult? error, Session? session) = await CheckOwnershipAsync(sessionId, principal, db);
        if (error is not null)
        {
            return error;
        }

        session!.ShareToken = ShareTokens.Create();
        AuditLog.Add(db, UserIdOf(principal), AuditLog.SessionShareReset, session.Id.ToString(), session.IsPublic ? "public" : "private");
        await db.SaveChangesAsync();
        return Results.Ok(new SharingResponse(session.IsPublic, session.ShareToken));
    }

    private static Guid? UserIdOf(ClaimsPrincipal principal) => CurrentUser.TryGetUserId(principal, out Guid id) ? id : null;

    /// <summary>
    /// The compiled replay file. Stored brotli-compressed and sent with <c>Content-Encoding: br</c>,
    /// so the browser hands the viewer the uncompressed bytes.
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

        return ServeReplay(session!, paths, httpContext);
    }

    /// <summary>Streams a Ready session's replay file; shared by the owner and the public share routes.</summary>
    internal static IResult ServeReplay(Session session, StoragePaths paths, HttpContext httpContext)
    {
        if (session.Status != SessionStatus.Ready || session.ReplayPath is null)
        {
            return Results.NotFound("The replay is not ready yet.");
        }

        string fullPath = paths.SessionFull(session.ReplayPath);
        if (!File.Exists(fullPath))
        {
            return Results.NotFound("The replay file is not on disk.");
        }

        httpContext.Response.Headers.CacheControl = "private, no-cache";
        httpContext.Response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);
        if (session.ReplayDecodedSizeBytes > 0)
        {
            // Content-Length is the compressed size (or absent when decompressing on the fly), so
            // the viewer reads the real size here for its download progress.
            httpContext.Response.Headers[SceneEndpoints.DecodedLengthHeader] =
                session.ReplayDecodedSizeBytes.ToString(CultureInfo.InvariantCulture);
        }

        string downloadName = $"{session.Id:N}.mbr";
        if (AcceptEncoding.Accepts(httpContext.Request, "br"))
        {
            httpContext.Response.Headers.ContentEncoding = "br";
            return Results.File(fullPath, "application/octet-stream", downloadName);
        }

        // A client that cannot decode brotli gets the plain bytes, decompressed as they are sent.
        var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Results.Stream(new BrotliStream(file, CompressionMode.Decompress), "application/octet-stream", downloadName);
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
            $"{session.ProjectId}/{session.SceneId}, recorded {session.CreatedAtUtc:u}, {session.RawLogSizeBytes + session.ReplaySizeBytes} bytes");
        await db.SaveChangesAsync();

        // Files after the row, as in SceneDeletion: a failure leaves an orphaned folder, never a row without files.
        Scenes.SceneDeletion.TryDeleteDirectory(paths.SessionFull(StoragePaths.SessionDir(session.ProjectId, session.Id)));
        return Results.NoContent();
    }

    private static async Task<(IResult? Error, Session? Session)> CheckOwnershipAsync(
        string sessionId,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db)
    {
        if (!Guid.TryParse(sessionId, out Guid id))
        {
            return (Results.NotFound(), null);
        }

        Session? session = await db.Sessions.FindAsync(id);
        if (session is null)
        {
            return (Results.NotFound(), null);
        }

        (IResult? error, Project? _) = await ProjectAccess.FindOwnedAsync(db, principal, session.ProjectId);
        return error is not null ? (error, null) : (null, session);
    }
}
