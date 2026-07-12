namespace MoodyBlues.Backend.Data;

/// <summary>Status of the background optimization pass over a scene's raw upload (see <c>Scenes/Processing</c>).</summary>
public enum SceneProcessingStatus
{
    Pending = 0,
    Processing = 1,
    Ready = 2,
    Failed = 3,
}

/// <summary>
/// Metadata for one developer's scene. Identity is the composite
/// <c>(DeveloperId, SceneId)</c> so two developers can each own their own
/// <c>SceneId</c> independently.
///
/// <see cref="Hash"/> is stored and compared as an opaque string -- the
/// backend does not parse the uploaded <c>.glb</c> or independently
/// recompute the hash this milestone (see Handshake plan notes).
/// </summary>
public sealed class Scene
{
    public required string DeveloperId { get; set; }

    public required string SceneId { get; set; }

    public required string Hash { get; set; }

    /// <summary>Path (relative to <see cref="Config.ServerConfig.ScenesDir"/>) of the raw, as-uploaded .glb -- always present and always servable, even before/if optimization hasn't finished.</summary>
    public required string RawGlbPath { get; set; }

    /// <summary>Size in bytes of the raw upload, captured for the "saved X%" stat once optimization completes.</summary>
    public long RawSizeBytes { get; set; }

    /// <summary>Status of the background Draco/KTX2 optimization pass (see <c>Scenes/Processing/SceneProcessingWorker</c>).</summary>
    public SceneProcessingStatus ProcessingStatus { get; set; } = SceneProcessingStatus.Pending;

    /// <summary>Path (relative to <see cref="Config.ServerConfig.ScenesDir"/>) of the optimized .glb, once <see cref="ProcessingStatus"/> is <see cref="SceneProcessingStatus.Ready"/>. Null until then.</summary>
    public string? OptimizedGlbPath { get; set; }

    /// <summary>Size in bytes of the optimized .glb, once available.</summary>
    public long? OptimizedSizeBytes { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>
    /// Dashboard-only display name set via <c>PATCH /api/scenes/{developerId}/{sceneId}</c> (see
    /// <see cref="MoodyBlues.Backend.Scenes.SceneEndpoints"/>). Purely cosmetic -- <see cref="SceneId"/>
    /// (the name Unity/the wire protocol actually use) never changes. Null until renamed at least once,
    /// in which case the frontend falls back to showing <see cref="SceneId"/>.
    /// </summary>
    public string? DisplayName { get; set; }
}
