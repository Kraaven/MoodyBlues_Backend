using Microsoft.EntityFrameworkCore;

namespace MoodyBlues.Backend.Data;

/// <summary>Disk space a developer (project) uses, from the sizes recorded in the database.</summary>
public static class StorageQuota
{
    public static async Task<long> UsedBytesAsync(MoodyBluesDbContext db, string developerId)
    {
        long scenes = await db.SceneRevisions
            .Where(r => r.DeveloperId == developerId)
            .SumAsync(r => r.RawSizeBytes + (r.OptimizedSizeBytes ?? 0));

        long sessions = await db.Sessions
            .Where(s => s.DeveloperId == developerId)
            .SumAsync(s => s.RawLogSizeBytes + s.ReplaySizeBytes);

        return scenes + sessions;
    }
}
