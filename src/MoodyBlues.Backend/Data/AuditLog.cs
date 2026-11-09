using MoodyBlues.Backend.Logging;

namespace MoodyBlues.Backend.Data;

/// <summary>
/// Writes <see cref="AuditEvent"/> rows (and a console line) for destructive and security-relevant
/// dashboard actions. Callers save the row together with the change it describes, so an audit
/// record exists exactly when the change happened.
/// </summary>
public static class AuditLog
{
    public const string SceneDelete = "scene.delete";
    public const string SceneVersionDelete = "scene.version.delete";
    public const string SceneArchiveDelete = "scene.archive.delete";
    public const string SessionDelete = "session.delete";
    public const string IngestKeyRotate = "project.ingestKey.rotate";

    /// <summary>Adds the row to the context; the caller's SaveChanges stores it.</summary>
    public static void Add(MoodyBluesDbContext db, Guid? userId, string action, string target, string detail)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            AtUtc = DateTime.UtcNow,
            UserId = userId,
            Action = action,
            Target = target.Length > 512 ? target[..512] : target,
            Detail = detail.Length > 2048 ? detail[..2048] : detail,
        });
        ConsoleLog.Info($"Audit: {action} {target} by user {userId?.ToString() ?? "-"}: {detail}");
    }
}
