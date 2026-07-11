using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using MoodyBlues.Backend.Auth;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;

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

        string fullPath = Path.Combine(config.ScenesDir, scene!.GlbPath);
        if (!File.Exists(fullPath))
        {
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
