using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using MoodyBlues.Backend.Auth;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Logging;

namespace MoodyBlues.Backend.Projects;

/// <summary>
/// Dashboard-facing project CRUD -- <c>/api/projects</c>. Every route requires a valid JWT
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

        var responses = new List<ProjectSummaryResponse>(projects.Count);
        foreach (Project project in projects)
        {
            int sceneCount = await db.Scenes.CountAsync(s => s.DeveloperId == project.DeveloperId);
            responses.Add(new ProjectSummaryResponse(project.Id, project.Name, project.DeveloperId, project.CreatedAtUtc, sceneCount));
        }

        return Results.Ok(responses);
    }

    /// <summary>Public so it can be exercised directly in tests without a live Postgres.</summary>
    public static async Task<IResult> CreateAsync(CreateProjectRequest request, ClaimsPrincipal principal, MoodyBluesDbContext db)
    {
        if (!CurrentUser.TryGetUserId(principal, out Guid userId))
        {
            return Results.Unauthorized();
        }

        string name = request.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.BadRequest("A project name is required.");
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

        db.Projects.Add(project);
        await db.SaveChangesAsync();

        // Pre-provision the Developer row so the dashboard has something to join against even
        // before the first Unity handshake for this developerId ever arrives.
        await DeveloperProvisioning.EnsureExistsAsync(db, developerId);

        ConsoleLog.Info($"Created project '{project.Name}' ({project.Id}) developerId={developerId} for user={userId}");

        return Results.Ok(new ProjectSummaryResponse(project.Id, project.Name, project.DeveloperId, project.CreatedAtUtc, SceneCount: 0));
    }

    /// <summary>Public so it can be exercised directly in tests without a live Postgres.</summary>
    public static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal principal, MoodyBluesDbContext db)
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

        var sceneResponses = scenes
            .Select(s => new ProjectSceneResponse(s.SceneId, s.DisplayName, s.UpdatedAtUtc))
            .ToList();

        return Results.Ok(new ProjectDetailResponse(project.Id, project.Name, project.DeveloperId, project.CreatedAtUtc, sceneResponses));
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
