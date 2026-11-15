using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MoodyBlues.Backend.Auth;
using MoodyBlues.Backend.Data;

namespace MoodyBlues.Backend.Projects;

/// <summary>
/// The one ownership rule of the dashboard: a project is reachable only by the user who owns it.
/// Everyone else gets 404, the same as for a project that does not exist, so existence is not
/// revealed. Project, scene and session routes all go through here.
/// </summary>
public static class ProjectAccess
{
    /// <summary>The caller's project, or the error to return: 401 without a user, 404 when it is not theirs.</summary>
    public static async Task<(IResult? Error, Project? Project)> FindOwnedAsync(
        MoodyBluesDbContext db, ClaimsPrincipal principal, Guid projectId)
    {
        if (!CurrentUser.TryGetUserId(principal, out Guid userId))
        {
            return (Results.Unauthorized(), null);
        }

        Project? project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId && p.UserId == userId);
        return project is null ? (Results.NotFound(), null) : (null, project);
    }
}
