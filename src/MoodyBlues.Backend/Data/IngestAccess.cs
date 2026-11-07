using Microsoft.EntityFrameworkCore;

namespace MoodyBlues.Backend.Data;

/// <summary>
/// Who may use the unauthenticated Unity-facing endpoints. Only developer IDs that belong to a
/// dashboard project are accepted. The ID ships inside every game build and so is effectively
/// public; this check stops anyone from creating data under made-up IDs, and the quotas in
/// <see cref="Config.ServerConfig"/> bound what a leaked ID can cost.
/// </summary>
public static class IngestAccess
{
    public const string UnknownDeveloperMessage =
        "Unknown developerId. Create a project in the MoodyBlues dashboard and copy its Developer ID into the scene config.";

    public static Task<bool> IsRegisteredDeveloperAsync(MoodyBluesDbContext db, string developerId) =>
        db.Projects.AnyAsync(p => p.DeveloperId == developerId);
}
