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

        List<string> developerIds = projects.Select(p => p.DeveloperId).ToList();
        Dictionary<string, int> sceneCounts = await db.Scenes
            .Where(s => developerIds.Contains(s.DeveloperId))
            .GroupBy(s => s.DeveloperId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        var responses = projects
            .Select(p => new ProjectSummaryResponse(
                p.Id, p.Name, p.DeveloperId, p.CreatedAtUtc, sceneCounts.GetValueOrDefault(p.DeveloperId), p.IngestKeyPrefix, p.IngestKeyCreatedAtUtc))
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

        string developerId = await GenerateUniqueDeveloperIdAsync(db);
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            DeveloperId = developerId,
            CreatedAtUtc = DateTime.UtcNow,
        };
        SetNewKey(project, protector, project.CreatedAtUtc);

        db.Projects.Add(project);
        await db.SaveChangesAsync();

        // Pre-provision the Developer row so the dashboard has something to join against even
        // before the first Unity handshake for this developerId ever arrives.
        await DeveloperProvisioning.EnsureExistsAsync(db, developerId);

        ConsoleLog.Info($"Created project '{project.Name}' ({project.Id}) developerId={developerId} for user={userId}");

        return Results.Ok(new ProjectSummaryResponse(
            project.Id, project.Name, project.DeveloperId, project.CreatedAtUtc, SceneCount: 0,
            project.IngestKeyPrefix, project.IngestKeyCreatedAtUtc));
    }

    /// <summary>Public so it can be exercised directly in tests without a live Postgres.</summary>
    public static async Task<IResult> GetAsync(
        Guid id, ClaimsPrincipal principal, MoodyBluesDbContext db, ServerConfig config, IngestKeyProtector protector)
    {
        if (!CurrentUser.TryGetUserId(principal, out Guid userId))
        {
            return Results.Unauthorized();
        }

        Project? project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (project is null)
        {
            return Results.NotFound();
        }

        List<Scene> scenes = await db.Scenes
            .Where(s => s.DeveloperId == project.DeveloperId)
            .OrderByDescending(s => s.UpdatedAtUtc)
            .ToListAsync();

        var sceneResponses = new List<Scenes.SceneResponse>(scenes.Count);
        foreach (Scene scene in scenes)
        {
            sceneResponses.Add(await Scenes.SceneEndpoints.ToResponseAsync(scene, db));
        }

        string? key = protector.TryUnprotect(project.IngestKeyEncrypted, project.Id);
        string? connectionString = key is null ? null : IngestAccess.ConnectionString(config, key);

        return Results.Ok(new ProjectDetailResponse(
            project.Id, project.Name, project.DeveloperId, project.CreatedAtUtc, sceneResponses,
            project.IngestKeyPrefix, project.IngestKeyCreatedAtUtc, connectionString));
    }

    /// <summary>
    /// Replaces the project's ingest key and returns the new connection string. The previous key
    /// stops working immediately, so every build and editor using it needs the new one.
    /// </summary>
    public static async Task<IResult> RotateIngestKeyAsync(
        Guid id, ClaimsPrincipal principal, MoodyBluesDbContext db, ServerConfig config, IngestKeyProtector protector)
    {
        if (!CurrentUser.TryGetUserId(principal, out Guid userId))
        {
            return Results.Unauthorized();
        }

        Project? project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (project is null)
        {
            return Results.NotFound();
        }

        bool replaced = project.IngestKeyHash is not null;
        DateTime now = DateTime.UtcNow;
        string key = SetNewKey(project, protector, now);
        AuditLog.Add(
            db,
            userId,
            AuditLog.IngestKeyRotate,
            project.DeveloperId,
            replaced ? $"replaced the ingest key; new key starts {project.IngestKeyPrefix}" : $"created ingest key {project.IngestKeyPrefix}");
        await db.SaveChangesAsync();

        return Results.Ok(new IngestKeyResponse(IngestAccess.ConnectionString(config, key), project.IngestKeyPrefix!, now));
    }

    /// <summary>Gives the project a fresh ingest key (hash, encrypted copy, prefix) and returns it.</summary>
    private static string SetNewKey(Project project, IngestKeyProtector protector, DateTime now)
    {
        string key = IngestAccess.NewKey();
        project.IngestKeyHash = IngestAccess.Hash(key);
        project.IngestKeyEncrypted = protector.Protect(key, project.Id);
        project.IngestKeyPrefix = IngestAccess.DisplayPrefix(key);
        project.IngestKeyCreatedAtUtc = now;
        return key;
    }

    private static async Task<string> GenerateUniqueDeveloperIdAsync(MoodyBluesDbContext db)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            string candidate = IdGenerator.NewToken();
            bool exists = await db.Projects.AnyAsync(p => p.DeveloperId == candidate);
            if (!exists)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not generate a unique developerId after 10 attempts.");
    }
}
