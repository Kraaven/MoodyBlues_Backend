using Microsoft.EntityFrameworkCore;

namespace MoodyBlues.Backend.Data;

/// <summary>
/// Shared "create this Developer row if it doesn't already exist" logic -- used both by
/// <c>POST /handshake</c> (auto-provisioning on first sight from Unity) and by
/// <c>POST /api/projects</c> (pre-provisioning so the dashboard has something to join against
/// even before the first handshake ever arrives).
/// </summary>
public static class DeveloperProvisioning
{
    public static async Task EnsureExistsAsync(MoodyBluesDbContext db, string developerId)
    {
        bool exists = await db.Developers.AnyAsync(d => d.Id == developerId);
        if (exists)
        {
            return;
        }

        db.Developers.Add(new Developer { Id = developerId, CreatedAtUtc = DateTime.UtcNow });
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Lost a race with a concurrent caller provisioning the same developer -- fine, it exists now.
        }
    }
}
