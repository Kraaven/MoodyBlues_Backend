using Microsoft.EntityFrameworkCore;

namespace MoodyBlues.Backend.Data;

/// <summary>Disk space a project uses, from the sizes recorded in the database.</summary>
public static class StorageQuota
{
    public static async Task<long> UsedBytesAsync(MoodyBluesDbContext db, Guid projectId)
    {
        long scenes = await db.SceneRevisions
            .Where(r => r.ProjectId == projectId)
            .SumAsync(r => r.RawSizeBytes + (r.OptimizedSizeBytes ?? 0));

        long sessions = await db.Sessions
            .Where(s => s.ProjectId == projectId)
            .SumAsync(s => s.RawLogSizeBytes + s.ReplaySizeBytes);

        return scenes + sessions;
    }
}
