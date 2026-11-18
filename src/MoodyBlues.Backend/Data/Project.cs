namespace MoodyBlues.Backend.Data;

/// <summary>
/// A dashboard project belonging to a <see cref="User"/>. <see cref="Id"/> keys scenes, sessions
/// and storage paths, and appears in dashboard routes. Unity never sends it; the ingest key
/// identifies the project instead.
/// </summary>
public sealed class Project
{
    public required Guid Id { get; set; }

    public required Guid UserId { get; set; }

    public required string Name { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// SHA-256 (hex) of the project's ingest key. The Unity client sends the key with every
    /// ingest call, and this hash (unique, indexed) is how the project is found. The key grants
    /// uploads only, never dashboard reads.
    /// </summary>
    public required string IngestKeyHash { get; set; }

    /// <summary>
    /// The key itself, encrypted by <see cref="IngestKeyProtector"/>, so the dashboard can show the
    /// connection string at any time.
    /// </summary>
    public required string IngestKeyEncrypted { get; set; }

    /// <summary>The key's first characters, so the dashboard can show which key is active.</summary>
    public required string IngestKeyPrefix { get; set; }

    public required DateTime IngestKeyCreatedAtUtc { get; set; }

    /// <summary>
    /// The project's template for share-viewer settings, an opaque JSON object owned by the frontend
    /// (<see cref="Sessions.ShareSettingsDocument"/> validates it). Null means the viewer's built-in default.
    /// A session that becomes public copies it; editing it never changes existing links.
    /// </summary>
    public string? ShareDefaultsJson { get; set; }
}
