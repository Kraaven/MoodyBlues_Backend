using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Scenes;
using MoodyBlues.Backend.Storage;

namespace MoodyBlues.Backend.Sessions;

/// <summary>
/// Anonymous, read-only access to a session its owner made public. The unguessable token is the
/// only credential; an unknown, malformed or private token is a plain 404 so nothing leaks.
/// </summary>
public static class ShareEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/share/{token}").AllowAnonymous().RequireRateLimiting(RateLimits.Share);
        group.MapGet("/", GetAsync);
        group.MapGet("/replay", DownloadReplayAsync);
        group.MapGet("/scene", DownloadSceneAsync);
    }

    public static async Task<IResult> GetAsync(string token, MoodyBluesDbContext db)
    {
        Session? session = await FindAsync(token, db);
        if (session is null)
        {
            return Results.NotFound();
        }

        var project = await db.Projects
            .Where(p => p.Id == session.ProjectId)
            .Select(p => new { p.Name, p.ShareDefaultsJson })
            .FirstAsync();
        var scene = await db.Scenes
            .Where(s => s.ProjectId == session.ProjectId && s.SceneId == session.SceneId)
            .Select(s => new { s.DisplayName, s.UnityName })
            .FirstOrDefaultAsync();
        int? version = await db.SceneRevisions
            .Where(r => r.ProjectId == session.ProjectId && r.SceneId == session.SceneId && r.Hash == session.SceneHash)
            .Select(r => (int?)r.Version)
            .FirstOrDefaultAsync();

        return Results.Ok(new SharedSessionResponse(
            SessionEndpoints.ToSharedDetail(session),
            project.Name,
            FirstNonBlank(scene?.DisplayName, scene?.UnityName) ?? session.SceneId,
            version,
            ShareSettingsDocument.ToElement(session.ShareSettingsJson ?? project.ShareDefaultsJson)));
    }

    public static async Task<IResult> DownloadReplayAsync(string token, MoodyBluesDbContext db, StoragePaths paths, HttpContext httpContext)
    {
        Session? session = await FindAsync(token, db);
        return session is null ? Results.NotFound() : SessionEndpoints.ServeReplay(session, paths, httpContext);
    }

    public static async Task<IResult> DownloadSceneAsync(string token, MoodyBluesDbContext db, StoragePaths paths, HttpContext httpContext)
    {
        Session? session = await FindAsync(token, db);
        return session is null
            ? Results.NotFound()
            : await SceneEndpoints.ServeRevisionAsync(session.ProjectId, session.SceneId, session.SceneHash, db, paths, httpContext);
    }

    private static string? FirstNonBlank(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : !string.IsNullOrWhiteSpace(second) ? second : null;

    private static async Task<Session?> FindAsync(string token, MoodyBluesDbContext db) =>
        ShareTokens.IsWellFormed(token)
            ? await db.Sessions.FirstOrDefaultAsync(s => s.ShareToken == token && s.IsPublic)
            : null;
}
