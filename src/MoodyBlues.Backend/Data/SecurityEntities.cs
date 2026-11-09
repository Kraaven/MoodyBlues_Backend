namespace MoodyBlues.Backend.Data;

/// <summary>
/// One dashboard refresh token (security plan phase 3). Only a SHA-256 of the token is stored.
/// Every refresh replaces the token with a new one in the same <see cref="FamilyId"/>; presenting
/// a token that was already replaced means it leaked, and the whole family is revoked.
/// </summary>
public sealed class RefreshToken
{
    public required Guid Id { get; set; }

    public required Guid UserId { get; set; }

    public required string TokenHash { get; set; }

    /// <summary>Shared by every token descended from one sign-in.</summary>
    public required Guid FamilyId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Set when this token was replaced by a refresh (as opposed to signed out or revoked).</summary>
    public Guid? ReplacedByTokenId { get; set; }

    /// <summary>Set when the token was used (replaced), signed out, or revoked as part of its family.</summary>
    public DateTime? RevokedAtUtc { get; set; }
}

/// <summary>
/// An audit record of a destructive or security-relevant dashboard action (security plan phase
/// 3): deletes, ingest key changes. Append-only; nothing in the app deletes these rows.
/// </summary>
public sealed class AuditEvent
{
    public required Guid Id { get; set; }

    public DateTime AtUtc { get; set; }

    /// <summary>The signed-in user who did it.</summary>
    public Guid? UserId { get; set; }

    /// <summary>For example <c>scene.delete</c>, <c>scene.version.delete</c>, <c>session.delete</c>, <c>project.ingestKey.rotate</c>.</summary>
    public required string Action { get; set; }

    /// <summary>What it was done to, for example <c>{developerId}/{sceneId}</c> or a session id.</summary>
    public required string Target { get; set; }

    /// <summary>A human-readable summary: counts, sizes, version numbers.</summary>
    public string Detail { get; set; } = string.Empty;
}
