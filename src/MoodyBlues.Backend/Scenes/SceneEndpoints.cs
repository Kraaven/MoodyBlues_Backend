using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using MoodyBlues.Backend.Auth;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Logging;

namespace MoodyBlues.Backend.Scenes;

/// <summary>
/// Dashboard-facing scene operations -- <c>/api/scenes/{developerId}/{sceneId}</c>. Separate from
/// the Unity-facing <see cref="SceneUploadEndpoints"/> (which stays unauthenticated). Every route
/// here requires a valid JWT and checks that the caller owns the <see cref="Data.Project"/> that
/// <c>developerId</c> belongs to.
/// </summary>
public static class SceneEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/scenes/{developerId}/{sceneId}").RequireAuthorization();

        group.MapPatch("/", RenameAsync);
        group.MapGet("/file", DownloadAsync);
    }

    /// <summary>Public so it can be exercised directly in tests without a live Postgres.</summary>
    public static async Task<IResult> RenameAsync(
        string developerId,
        string sceneId,
        RenameSceneRequest request,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db)
    {
        (IResult? authError, Scene? scene) = await CheckOwnershipAsync(developerId, sceneId, principal, db);
        if (authError is not null)
        {
            return authError;
        }

        string? displayName = request.DisplayName?.Trim();
        scene!.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName;
        await db.SaveChangesAsync();

        return Results.Ok(new SceneResponse(scene.SceneId, scene.DisplayName, scene.UpdatedAtUtc));
    }

    /// <summary>Public so it can be exercised directly in tests without a live Postgres.</summary>
    public static async Task<IResult> DownloadAsync(
        string developerId,
        string sceneId,
        ClaimsPrincipal principal,
        MoodyBluesDbContext db,
        ServerConfig config)
    {
        (IResult? authError, Scene? scene) = await CheckOwnershipAsync(developerId, sceneId, principal, db);
        if (authError is not null)
        {
            return authError;
        }

        // Prefer the optimized (Draco/KTX2) output once the background pass has finished; fall back to
        // the raw upload otherwise -- viewing a scene should never block on/fail because of optimization.
        // Also falls back if the DB says "Ready" but the file isn't actually on disk (e.g. a volume
        // wiped between passes) -- the raw upload is the one file that's always guaranteed to exist.
        string? optimizedFullPath = scene!.ProcessingStatus == SceneProcessingStatus.Ready && scene.OptimizedGlbPath is not null
            ? Path.Combine(config.ScenesDir, scene.OptimizedGlbPath)
            : null;

        string fullPath = optimizedFullPath is not null && File.Exists(optimizedFullPath)
            ? optimizedFullPath
            : Path.Combine(config.ScenesDir, scene.RawGlbPath);

        if (!File.Exists(fullPath))
        {
            ConsoleLog.Error(
                $"Scene download: file missing on disk for developer={developerId} scene={sceneId} "
                + $"status={scene.ProcessingStatus} expectedPath={fullPath}");
            return Results.NotFound("Scene file is not on disk.");
        }

        return Results.File(fullPath, contentType: "model/gltf-binary", fileDownloadName: $"{scene.SceneId}.glb", enableRangeProcessing: true);
    }

    private static async Task<(IResult? Error, Scene? Scene)> CheckOwnershipAsync(
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
            return (Results.BadRequest("developerId and sceneId must not contain path separators."), null);
        }

        bool owns = await db.Projects.AnyAsync(p => p.DeveloperId == developerId && p.UserId == userId);
        if (!owns)
        {
            return (Results.NotFound(), null);
        }

        Scene? scene = await db.Scenes.FindAsync(developerId, sceneId);
        if (scene is null)
        {
            return (Results.NotFound(), null);
        }

        return (null, scene);
    }
}
