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
using MoodyBlues.Backend.Scenes;
using MoodyBlues.Backend.Sessions;

namespace MoodyBlues.Backend.Projects;

/// <summary>
/// Dashboard-facing project routes -- <c>/api/projects</c>. Every route requires a signed-in user
/// (see <see cref="Auth.AuthEndpoints"/>) and only ever operates on the caller's own projects.
/// </summary>
public static class ProjectEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/projects").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("/{id:guid}/ingest-key", RotateIngestKeyAsync);
        group.MapPut("/{id:guid}/share-defaults", SetShareDefaultsAsync);
    }

    /// <summary>Public so it can be exercised directly in tests without a live Postgres.</summary>
    public static async Task<IResult> ListAsync(ClaimsPrincipal principal, MoodyBluesDbContext db)
    {
        if (!CurrentUser.TryGetUserId(principal, out Guid userId))
        {
            return Results.Unauthorized();
        }

        List<Project> projects = await db.Projects
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedAtUtc)
            .ToListAsync();

        List<Guid> projectIds = projects.Select(p => p.Id).ToList();
        Dictionary<Guid, int> sceneCounts = await db.Scenes
            .Where(s => projectIds.Contains(s.ProjectId))
            .GroupBy(s => s.ProjectId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        var responses = projects
            .Select(p => new ProjectSummaryResponse(
                p.Id, p.Name, p.CreatedAtUtc, sceneCounts.GetValueOrDefault(p.Id), p.IngestKeyPrefix, p.IngestKeyCreatedAtUtc))
            .ToList();

        return Results.Ok(responses);
    }

    /// <summary>
    /// Creates a project with its first ingest key. The project page shows the connection string
    /// built from it (<see cref="GetAsync"/>).
    /// </summary>
    public static async Task<IResult> CreateAsync(
        CreateProjectRequest request, ClaimsPrincipal principal, MoodyBluesDbContext db, IngestKeyProtector protector)
    {
        if (!CurrentUser.TryGetUserId(principal, out Guid userId))
        {
            return Results.Unauthorized();
        }

        string name = request.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
        {
            return Results.BadRequest("A project name of 1-200 characters is required.");
        }

        var id = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        IngestKeyMaterial key = IngestKeyMaterial.Create(protector, id);
        var project = new Project
        {
            Id = id,
            UserId = userId,
            Name = name,
            CreatedAtUtc = now,
            IngestKeyHash = key.Hash,
            IngestKeyEncrypted = key.Encrypted,
            IngestKeyPrefix = key.Prefix,
            IngestKeyCreatedAtUtc = now,
        };

        db.Projects.Add(project);
        await db.SaveChangesAsync();

        ConsoleLog.Info($"Created project '{project.Name}' ({project.Id}) for user={userId}");

        return Results.Ok(new ProjectSummaryResponse(
            project.Id, project.Name, project.CreatedAtUtc, SceneCount: 0,
            project.IngestKeyPrefix, project.IngestKeyCreatedAtUtc));
    }

    /// <summary>Public so it can be exercised directly in tests without a live Postgres.</summary>
    public static async Task<IResult> GetAsync(
        Guid id, ClaimsPrincipal principal, MoodyBluesDbContext db, ServerConfig config, IngestKeyProtector protector)
    {
        (IResult? error, Project? project) = await ProjectAccess.FindOwnedAsync(db, principal, id);
        if (error is not null)
        {
            return error;
        }

        List<Scene> scenes = await db.Scenes
            .Where(s => s.ProjectId == id)
            .OrderByDescending(s => s.UpdatedAtUtc)
            .ToListAsync();

        // One query for every scene's current revision, not one per scene.
        Dictionary<string, SceneRevision> latest = await db.Scenes
            .Where(s => s.ProjectId == id)
            .Join(
                db.SceneRevisions,
                s => new { s.ProjectId, s.SceneId, Hash = s.LatestHash },
                r => new { r.ProjectId, r.SceneId, r.Hash },
                (s, r) => r)
            .ToDictionaryAsync(r => r.SceneId, StringComparer.Ordinal);

        List<SceneResponse> sceneResponses = scenes
            .Select(scene => SceneEndpoints.ToResponse(scene, latest.GetValueOrDefault(scene.SceneId)))
            .ToList();

        string? key = protector.TryUnprotect(project!.IngestKeyEncrypted, project.Id);
        string? connectionString = key is null ? null : IngestAccess.ConnectionString(config, key);

        return Results.Ok(new ProjectDetailResponse(
            project.Id, project.Name, project.CreatedAtUtc, sceneResponses,
            project.IngestKeyPrefix, project.IngestKeyCreatedAtUtc, connectionString,
            ShareSettingsDocument.ToElement(project.ShareDefaultsJson)));
    }

    /// <summary>
    /// Replaces (or, with null, clears) the template that new share links copy. Existing links keep the
    /// settings they already have.
    /// </summary>
    public static async Task<IResult> SetShareDefaultsAsync(
        Guid id, ShareSettingsRequest request, ClaimsPrincipal principal, MoodyBluesDbContext db)
    {
        (IResult? error, Project? project) = await ProjectAccess.FindOwnedAsync(db, principal, id);
        if (error is not null)
        {
            return error;
        }

        (string? json, string? invalid) = ShareSettingsDocument.Validate(request.Settings);
        if (invalid is not null)
        {
            return Results.BadRequest(invalid);
        }

        project!.ShareDefaultsJson = json;
        AuditLog.Add(
            db,
            CurrentUser.TryGetUserId(principal, out Guid userId) ? userId : null,
            AuditLog.ProjectShareDefaults,
            project.Id.ToString(),
            json is null ? "cleared" : $"updated ({json.Length} chars)");
        await db.SaveChangesAsync();

        return Results.Ok(new ShareSettingsResponse(ShareSettingsDocument.ToElement(json)));
    }

    /// <summary>
    /// Replaces the project's ingest key and returns the new connection string. The previous key
    /// stops working immediately, so every build and editor using it needs the new one.
    /// </summary>
    public static async Task<IResult> RotateIngestKeyAsync(
        Guid id, ClaimsPrincipal principal, MoodyBluesDbContext db, ServerConfig config, IngestKeyProtector protector)
    {
        (IResult? error, Project? project) = await ProjectAccess.FindOwnedAsync(db, principal, id);
        if (error is not null)
        {
            return error;
        }

        DateTime now = DateTime.UtcNow;
        IngestKeyMaterial key = IngestKeyMaterial.Create(protector, project!.Id);
        project.IngestKeyHash = key.Hash;
        project.IngestKeyEncrypted = key.Encrypted;
        project.IngestKeyPrefix = key.Prefix;
        project.IngestKeyCreatedAtUtc = now;
        AuditLog.Add(
            db,
            CurrentUser.TryGetUserId(principal, out Guid userId) ? userId : null,
            AuditLog.IngestKeyRotate,
            project.Id.ToString(),
            $"replaced the ingest key; new key starts {key.Prefix}");
        await db.SaveChangesAsync();

        return Results.Ok(new IngestKeyResponse(IngestAccess.ConnectionString(config, key.Key), key.Prefix, now));
    }

    /// <summary>A fresh ingest key and everything the project row stores about it.</summary>
    private readonly record struct IngestKeyMaterial(string Key, string Hash, string Encrypted, string Prefix)
    {
        public static IngestKeyMaterial Create(IngestKeyProtector protector, Guid projectId)
        {
            string key = IngestAccess.NewKey();
            return new IngestKeyMaterial(
                key, Hashing.Sha256Hex(key), protector.Protect(key, projectId), IngestAccess.DisplayPrefix(key));
        }
    }
}
