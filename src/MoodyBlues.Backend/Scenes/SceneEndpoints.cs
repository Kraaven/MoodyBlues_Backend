using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using MoodyBlues.Backend.Auth;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Ingest;
using MoodyBlues.Backend.Logging;
using MoodyBlues.Backend.Projects;
using MoodyBlues.Backend.Storage;

namespace MoodyBlues.Backend.Scenes;

/// <summary>
/// Dashboard scene operations under <c>/api/projects/{projectId}/scenes/{sceneId}</c>. Every route
/// needs a JWT and checks the caller owns the project (<see cref="ProjectAccess"/>); non-owners
/// get 404 so scene existence is not revealed.
/// </summary>
public static class SceneEndpoints
{
    /// <summary>Size of the GLB after any Content-Encoding is removed; listed in the CORS policy so the dashboard can read it.</summary>
    public const string DecodedLengthHeader = "X-MoodyBlues-Decoded-Length";

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}/scenes/{sceneId}").RequireAuthorization();
        group.MapGet("/", GetAsync);
        group.MapPatch("/", RenameAsync);
        group.MapDelete("/", DeleteSceneAsync);
        group.MapGet("/file", DownloadAsync);
        group.MapDelete("/versions/{version:int}", DeleteVersionAsync);
        group.MapDelete("/archive", DeleteArchiveAsync);
    }

    /// <summary>The scene with its current version and every archived version.</summary>
    public static async Task<IResult> GetAsync(Guid projectId, string sceneId, ClaimsPrincipal principal, MoodyBluesDbContext db)
    {
        (IResult? error, Scene? scene) = await CheckOwnershipAsync(projectId, sceneId, principal, db);
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
        Guid projectId,
        string sceneId,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db,
        StoragePaths paths,
        ActiveConnections connections)
    {
        (IResult? error, Scene? _) = await CheckOwnershipAsync(projectId, sceneId, principal, db);
        if (error is not null)
        {
            return error;
        }

        List<SceneRevision> all = await db.SceneRevisions.Where(r => r.ProjectId == projectId && r.SceneId == sceneId).ToListAsync();
        DeletionResult result = await SceneDeletion.DeleteAsync(db, paths, connections, projectId, sceneId, all, deleteScene: true, UserIdOf(principal), AuditLog.SceneDelete);
        return Results.Ok(new DeleteResponse(result.Versions, result.Sessions, result.Bytes));
    }

    /// <summary>Deletes one archived version and its sessions. The current version cannot be deleted on its own.</summary>
    public static async Task<IResult> DeleteVersionAsync(
        Guid projectId,
        string sceneId,
        int version,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db,
        StoragePaths paths,
        ActiveConnections connections)
    {
        (IResult? error, Scene? scene) = await CheckOwnershipAsync(projectId, sceneId, principal, db);
        if (error is not null)
        {
            return error;
        }

        SceneRevision? revision = await db.SceneRevisions
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.SceneId == sceneId && r.Version == version);
        if (revision is null)
        {
            return Results.NotFound("That version does not exist.");
        }

        if (revision.Hash == scene!.LatestHash)
        {
            return Results.Conflict("This is the scene's current version. Delete the scene instead.");
        }

        DeletionResult result = await SceneDeletion.DeleteAsync(db, paths, connections, projectId, sceneId, [revision], deleteScene: false, UserIdOf(principal), AuditLog.SceneVersionDelete);
        return Results.Ok(new DeleteResponse(result.Versions, result.Sessions, result.Bytes));
    }

    /// <summary>Deletes every archived version and their sessions, keeping the current version.</summary>
    public static async Task<IResult> DeleteArchiveAsync(
        Guid projectId,
        string sceneId,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db,
        StoragePaths paths,
        ActiveConnections connections)
    {
        (IResult? error, Scene? scene) = await CheckOwnershipAsync(projectId, sceneId, principal, db);
        if (error is not null)
        {
            return error;
        }

        List<SceneRevision> archived = await db.SceneRevisions
            .Where(r => r.ProjectId == projectId && r.SceneId == sceneId && r.Hash != scene!.LatestHash)
            .ToListAsync();

        DeletionResult result = await SceneDeletion.DeleteAsync(db, paths, connections, projectId, sceneId, archived, deleteScene: false, UserIdOf(principal), AuditLog.SceneArchiveDelete);
        return Results.Ok(new DeleteResponse(result.Versions, result.Sessions, result.Bytes));
    }

    public static async Task<IResult> RenameAsync(
        Guid projectId,
        string sceneId,
        RenameSceneRequest request,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db)
    {
        (IResult? error, Scene? scene) = await CheckOwnershipAsync(projectId, sceneId, principal, db);
        if (error is not null)
        {
            return error;
        }

        string? displayName = request.DisplayName?.Trim();
        scene!.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName[..Math.Min(displayName.Length, 200)];
        await db.SaveChangesAsync();

        SceneRevision? latest = await db.SceneRevisions.FindAsync(projectId, sceneId, scene.LatestHash);
        return Results.Ok(ToResponse(scene, latest));
    }

    /// <summary>
    /// Serves a scene revision's GLB: <c>?hash=</c> selects a revision (sessions pass the hash
    /// they were recorded with), otherwise the latest. The optimized file is preferred once it
    /// exists; the raw upload is the fallback. Revisions never change, so the response is cacheable.
    /// </summary>
    public static async Task<IResult> DownloadAsync(
        Guid projectId,
        string sceneId,
        string? hash,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db,
        StoragePaths paths,
        HttpContext httpContext)
    {
        (IResult? error, Scene? scene) = await CheckOwnershipAsync(projectId, sceneId, principal, db);
        if (error is not null)
        {
            return error;
        }

        string revisionHash = string.IsNullOrEmpty(hash) ? scene!.LatestHash : hash;
        if (!PathSegments.IsSceneHash(revisionHash))
        {
            return Results.BadRequest("hash must be 64 lowercase hex characters.");
        }

        return await ServeRevisionAsync(projectId, sceneId, revisionHash, db, paths, httpContext);
    }

    /// <summary>
    /// Streams one revision's GLB (no ownership check: callers authorize first); shared with the
    /// public share route. The optimized file goes out as its pre-gzipped copy when the client
    /// accepts gzip.
    /// </summary>
    internal static async Task<IResult> ServeRevisionAsync(
        Guid projectId,
        string sceneId,
        string revisionHash,
        MoodyBluesDbContext db,
        StoragePaths paths,
        HttpContext httpContext)
    {
        SceneRevision? revision = await db.SceneRevisions.FindAsync(projectId, sceneId, revisionHash);
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
            ConsoleLog.Error($"Scene download: file missing on disk for {projectId}/{sceneId}/{revisionHash}: {fullPath}");
            return Results.NotFound("Scene file is not on disk.");
        }

        httpContext.Response.Headers[DecodedLengthHeader] = new FileInfo(fullPath).Length.ToString(CultureInfo.InvariantCulture);
        bool isOptimized = optimized is not null && fullPath == optimized;
        if (!isOptimized)
        {
            return Results.File(fullPath, "model/gltf-binary", $"{sceneId}.glb", enableRangeProcessing: true);
        }

        // Only cache once the optimized file is being served; before that the response changes.
        httpContext.Response.Headers.CacheControl = "private, max-age=31536000, immutable";

        string gzipPath = Path.Combine(Path.GetDirectoryName(fullPath)!, StoragePaths.OptimizedGlbGzipFile);
        if (!File.Exists(gzipPath))
        {
            return Results.File(fullPath, "model/gltf-binary", $"{sceneId}.glb", enableRangeProcessing: true);
        }

        httpContext.Response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);
        if (!AcceptEncoding.Accepts(httpContext.Request, "gzip"))
        {
            return Results.File(fullPath, "model/gltf-binary", $"{sceneId}.glb", enableRangeProcessing: true);
        }

        // The pre-gzipped copy, like the replay file: the browser decodes it, so the viewer still
        // reads plain GLB bytes. Content-Length is the gzip size, hence DecodedLengthHeader. A byte
        // range into the compressed bytes means nothing to the client, so none is offered.
        httpContext.Response.Headers.ContentEncoding = "gzip";
        return Results.File(gzipPath, "model/gltf-binary", $"{sceneId}.glb", enableRangeProcessing: false);
    }

    /// <summary>
    /// The list/summary form of a scene. Takes its current revision so a project's scenes can be
    /// built from one query instead of one per scene (null while the revision row is missing).
    /// </summary>
    internal static SceneResponse ToResponse(Scene scene, SceneRevision? latest) => new(
        scene.SceneId,
        scene.DisplayName,
        scene.UnityName,
        scene.UpdatedAtUtc,
        scene.LatestHash,
        latest?.ServableSizeBytes ?? 0,
        latest?.Status ?? SceneProcessingStatus.Pending,
        latest?.Version ?? 0);

    internal static async Task<SceneDetailResponse> ToDetailAsync(Scene scene, MoodyBluesDbContext db)
    {
        List<SceneRevision> revisions = await db.SceneRevisions
            .Where(r => r.ProjectId == scene.ProjectId && r.SceneId == scene.SceneId)
            .OrderByDescending(r => r.Version)
            .ToListAsync();

        var sessionStats = await db.Sessions
            .Where(s => s.ProjectId == scene.ProjectId && s.SceneId == scene.SceneId)
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
        long total = revisions.Sum(r => r.RawSizeBytes + (r.OptimizedSizeBytes ?? 0) + (r.OptimizedGzipSizeBytes ?? 0)) + sessionStats.Sum(s => s.Bytes);

        SceneRevision? current = revisions.FirstOrDefault(r => r.Hash == scene.LatestHash);
        return new SceneDetailResponse(
            scene.ProjectId,
            scene.SceneId,
            scene.DisplayName,
            scene.UnityName,
            scene.CreatedAtUtc,
            scene.UpdatedAtUtc,
            current is null ? null : ToVersion(current),
            revisions.Where(r => r.Hash != scene.LatestHash).Select(ToVersion).ToList(),
            awaiting,
            total);
    }

    private static Guid? UserIdOf(ClaimsPrincipal principal) => CurrentUser.TryGetUserId(principal, out Guid id) ? id : null;

    internal static async Task<(IResult? Error, Scene? Scene)> CheckOwnershipAsync(
        Guid projectId,
        string sceneId,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db)
    {
        (IResult? error, Project? _) = await ProjectAccess.FindOwnedAsync(db, principal, projectId);
        if (error is not null)
        {
            return (error, null);
        }

        if (!PathSegments.IsSafe(sceneId))
        {
            return (Results.BadRequest("Invalid sceneId."), null);
        }

        Scene? scene = await db.Scenes.FindAsync(projectId, sceneId);
        return scene is null ? (Results.NotFound(), null) : (null, scene);
    }
}
