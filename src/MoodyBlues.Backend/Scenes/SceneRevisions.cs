using Microsoft.EntityFrameworkCore;
using MoodyBlues.Backend.Data;

namespace MoodyBlues.Backend.Scenes;

/// <summary>Lookups on <see cref="SceneRevision"/> shared by the ingest and dashboard endpoints.</summary>
public static class SceneRevisions
{
    /// <summary>Whether this scene version has been uploaded. Revisions never change, so this answers "is the upload still needed".</summary>
    public static Task<bool> ExistsAsync(MoodyBluesDbContext db, Guid projectId, string sceneId, string hash) =>
        db.SceneRevisions.AnyAsync(r => r.ProjectId == projectId && r.SceneId == sceneId && r.Hash == hash);
}
