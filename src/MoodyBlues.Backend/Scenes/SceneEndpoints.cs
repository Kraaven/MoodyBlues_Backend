using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MoodyBlues.Backend.Auth;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Ingest;
using MoodyBlues.Backend.Logging;
using MoodyBlues.Backend.Storage;

namespace MoodyBlues.Backend.Scenes;

/// <summary>
/// Dashboard scene operations under <c>/api/scenes/{developerId}/{sceneId}</c>. Every route needs
/// a JWT and checks the caller owns the project that <c>developerId</c> belongs to; non-owners
/// get 404 so scene existence is not revealed.
/// </summary>
public static class SceneEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/scenes/{developerId}/{sceneId}").RequireAuthorization();
        group.MapGet("/", GetAsync);
        group.MapPatch("/", RenameAsync);
        group.MapDelete("/", DeleteSceneAsync);
        group.MapGet("/file", DownloadAsync);
        group.MapDelete("/versions/{version:int}", DeleteVersionAsync);
        group.MapDelete("/archive", DeleteArchiveAsync);
    }

    /// <summary>The scene with its current version and every archived version.</summary>
    public static async Task<IResult> GetAsync(string developerId, string sceneId, ClaimsPrincipal principal, MoodyBluesDbContext db)
    {
        (IResult? error, Scene? scene) = await CheckOwnershipAsync(developerId, sceneId, principal, db);
        if (error is not null)
        {
            return error;
        }

        return Results.Ok(await ToDetailAsync(scene!, db));
    }

    /// <summary>
    /// Deletes the scene: every version, every session recorded in it (live recordings are
    /// closed first) and all their files. The next upload from any build starts it again at v1.
    /// </summary>
    public static async Task<IResult> DeleteSceneAsync(
        string developerId,
        string sceneId,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db,
        StoragePaths paths,
        ActiveConnections connections)
    {
        (IResult? error, Scene? _) = await CheckOwnershipAsync(developerId, sceneId, principal, db);
        if (error is not null)
        {
            return error;
        }

        List<SceneRevision> all = await db.SceneRevisions.Where(r => r.DeveloperId == developerId && r.SceneId == sceneId).ToListAsync();
        DeletionResult result = await SceneDeletion.DeleteAsync(db, paths, connections, developerId, sceneId, all, deleteScene: true);
        return Results.Ok(new DeleteResponse(result.Versions, result.Sessions, result.Bytes));
    }

    /// <summary>Deletes one archived version and its sessions. The current version cannot be deleted on its own.</summary>
    public static async Task<IResult> DeleteVersionAsync(
        string developerId,
        string sceneId,
        int version,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db,
        StoragePaths paths,
        ActiveConnections connections)
    {
        (IResult? error, Scene? scene) = await CheckOwnershipAsync(developerId, sceneId, principal, db);
        if (error is not null)
        {
            return error;
        }

        SceneRevision? revision = await db.SceneRevisions
            .FirstOrDefaultAsync(r => r.DeveloperId == developerId && r.SceneId == sceneId && r.Version == version);
        if (revision is null)
        {
            return Results.NotFound("That version does not exist.");
        }

        if (revision.Hash == scene!.LatestHash)
        {
            return Results.Conflict("This is the scene's current version. Delete the scene instead.");
        }

        DeletionResult result = await SceneDeletion.DeleteAsync(db, paths, connections, developerId, sceneId, [revision], deleteScene: false);
        return Results.Ok(new DeleteResponse(result.Versions, result.Sessions, result.Bytes));
    }

    /// <summary>Deletes every archived version and their sessions, keeping the current version.</summary>
    public static async Task<IResult> DeleteArchiveAsync(
        string developerId,
        string sceneId,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db,
        StoragePaths paths,
        ActiveConnections connections)
    {
        (IResult? error, Scene? scene) = await CheckOwnershipAsync(developerId, sceneId, principal, db);
        if (error is not null)
        {
            return error;
        }

        List<SceneRevision> archived = await db.SceneRevisions
            .Where(r => r.DeveloperId == developerId && r.SceneId == sceneId && r.Hash != scene!.LatestHash)
            .ToListAsync();

        DeletionResult result = await SceneDeletion.DeleteAsync(db, paths, connections, developerId, sceneId, archived, deleteScene: false);
        return Results.Ok(new DeleteResponse(result.Versions, result.Sessions, result.Bytes));
    }

    public static async Task<IResult> RenameAsync(
        string developerId,
        string sceneId,
        RenameSceneRequest request,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db)
    {
        (IResult? error, Scene? scene) = await CheckOwnershipAsync(developerId, sceneId, principal, db);
        if (error is not null)
        {
            return error;
        }

        string? displayName = request.DisplayName?.Trim();
        scene!.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName[..Math.Min(displayName.Length, 200)];
        await db.SaveChangesAsync();

        return Results.Ok(await ToResponseAsync(scene, db));
    }

    /// <summary>
    /// Serves a scene revision's GLB: <c>?hash=</c> selects a revision (sessions pass the hash
    /// they were recorded with), otherwise the latest. The optimized file is preferred once it
    /// exists; the raw upload is the fallback. Revisions never change, so the response is cacheable.
    /// </summary>
    public static async Task<IResult> DownloadAsync(
        string developerId,
        string sceneId,
        string? hash,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db,
        StoragePaths paths,
        HttpContext httpContext)
    {
        (IResult? error, Scene? scene) = await CheckOwnershipAsync(developerId, sceneId, principal, db);
        if (error is not null)
        {
            return error;
        }

        string revisionHash = string.IsNullOrEmpty(hash) ? scene!.LatestHash : hash;
        if (!PathSegments.IsSceneHash(revisionHash))
        {
            return Results.BadRequest("hash must be 64 lowercase hex characters.");
        }

        SceneRevision? revision = await db.SceneRevisions.FindAsync(developerId, sceneId, revisionHash);
        if (revision is null)
        {
            return Results.NotFound("That scene revision was never uploaded.");
        }

        string? optimized = revision is { Status: SceneProcessingStatus.Ready, OptimizedGlbPath: not null }
            ? paths.SceneFull(revision.OptimizedGlbPath)
            : null;
        string fullPath = optimized is not null && File.Exists(optimized) ? optimized : paths.SceneFull(revision.RawGlbPath);

        if (!File.Exists(fullPath))
        {
            ConsoleLog.Error($"Scene download: file missing on disk for {developerId}/{sceneId}/{revisionHash}: {fullPath}");
            return Results.NotFound("Scene file is not on disk.");
        }

        // Only cache once the optimized file is being served; before that the response changes.
        if (optimized is not null && fullPath == optimized)
        {
            httpContext.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        }

        // Results.File needs an absolute path (a relative one is resolved against wwwroot).
        return Results.File(fullPath, "model/gltf-binary", $"{sceneId}.glb", enableRangeProcessing: true);
    }

    internal static async Task<SceneResponse> ToResponseAsync(Scene scene, MoodyBluesDbContext db)
    {
        SceneRevision? latest = await db.SceneRevisions.FindAsync(scene.DeveloperId, scene.SceneId, scene.LatestHash);
        int revisionCount = await db.SceneRevisions.CountAsync(r => r.DeveloperId == scene.DeveloperId && r.SceneId == scene.SceneId);
        return new SceneResponse(
            scene.SceneId,
            scene.DisplayName,
            scene.UpdatedAtUtc,
            scene.LatestHash,
            latest?.ServableSizeBytes ?? 0,
            latest?.Status ?? SceneProcessingStatus.Pending,
            revisionCount,
            latest?.Version ?? 0);
    }

    internal static async Task<SceneDetailResponse> ToDetailAsync(Scene scene, MoodyBluesDbContext db)
    {
        List<SceneRevision> revisions = await db.SceneRevisions
            .Where(r => r.DeveloperId == scene.DeveloperId && r.SceneId == scene.SceneId)
            .OrderByDescending(r => r.Version)
            .ToListAsync();

        var sessionStats = await db.Sessions
            .Where(s => s.DeveloperId == scene.DeveloperId && s.SceneId == scene.SceneId)
            .GroupBy(s => s.SceneHash)
            .Select(g => new { Hash = g.Key, Count = g.Count(), Bytes = g.Sum(s => s.RawLogSizeBytes + s.ReplaySizeBytes) })
            .ToListAsync();
        var statsByHash = sessionStats.ToDictionary(x => x.Hash, StringComparer.Ordinal);

        SceneVersionResponse ToVersion(SceneRevision r)
        {
            statsByHash.TryGetValue(r.Hash, out var stats);
            return new SceneVersionResponse(
                r.Version,
                r.Hash,
                r.CreatedAtUtc,
                r.Hash == scene.LatestHash,
                r.ServableSizeBytes,
                r.Status,
                r.ErrorMessage,
                r.SkippedMeshCount,
                r.TrackedObjectCount,
                stats?.Count ?? 0,
                stats?.Bytes ?? 0);
        }

        var known = revisions.Select(r => r.Hash).ToHashSet(StringComparer.Ordinal);
        int awaiting = sessionStats.Where(s => !known.Contains(s.Hash)).Sum(s => s.Count);
        long total = revisions.Sum(r => r.RawSizeBytes + (r.OptimizedSizeBytes ?? 0)) + sessionStats.Sum(s => s.Bytes);

        SceneRevision? current = revisions.FirstOrDefault(r => r.Hash == scene.LatestHash);
        return new SceneDetailResponse(
            scene.DeveloperId,
            scene.SceneId,
            scene.DisplayName,
            scene.CreatedAtUtc,
            scene.UpdatedAtUtc,
            current is null ? null : ToVersion(current),
            revisions.Where(r => r.Hash != scene.LatestHash).Select(ToVersion).ToList(),
            awaiting,
            total);
    }

    internal static async Task<(IResult? Error, Scene? Scene)> CheckOwnershipAsync(
        string developerId,
        string sceneId,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db)
    {
        if (!CurrentUser.TryGetUserId(principal, out Guid userId))
        {
            return (Results.Unauthorized(), null);
        }

        if (!PathSegments.IsSafe(developerId) || !PathSegments.IsSafe(sceneId))
        {
            return (Results.BadRequest("Invalid developerId or sceneId."), null);
        }

        bool owns = await db.Projects.AnyAsync(p => p.DeveloperId == developerId && p.UserId == userId);
        if (!owns)
        {
            return (Results.NotFound(), null);
        }

        Scene? scene = await db.Scenes.FindAsync(developerId, sceneId);
        return scene is null ? (Results.NotFound(), null) : (null, scene);
    }
}
