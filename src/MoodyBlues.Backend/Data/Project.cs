namespace MoodyBlues.Backend.Data;

/// <summary>
/// A dashboard project belonging to a <see cref="User"/>. <see cref="DeveloperId"/> is the
/// project's internal name: it keys scenes, sessions and storage paths, and appears in dashboard
/// routes. Unity never sees it; the ingest key identifies the project instead.
/// </summary>
public sealed class Project
{
    public required Guid Id { get; set; }

    public required Guid UserId { get; set; }

    public required string Name { get; set; }

    public required string DeveloperId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// SHA-256 (hex) of the project's ingest key. The Unity client sends the key with every
    /// ingest call, and this hash (unique, indexed) is how the project is found. The key grants
    /// uploads only, never dashboard reads.
    /// </summary>
    public string? IngestKeyHash { get; set; }

    /// <summary>
    /// The key itself, encrypted by <see cref="IngestKeyProtector"/>, so the dashboard can show the
    /// connection string at any time. Null for keys issued before this was stored.
    /// </summary>
    public string? IngestKeyEncrypted { get; set; }

    /// <summary>The key's first characters, so the dashboard can show which key is active.</summary>
    public string? IngestKeyPrefix { get; set; }

    public DateTime? IngestKeyCreatedAtUtc { get; set; }
}
