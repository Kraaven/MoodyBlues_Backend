namespace MoodyBlues.Backend.Data;

/// <summary>Status of the background processing of one uploaded scene revision.</summary>
public enum SceneProcessingStatus
{
    /// <summary>Uploaded, waiting for the scene worker.</summary>
    Pending = 0,

    /// <summary>The worker is building the object registry and/or optimizing the GLB.</summary>
    Processing = 1,

    /// <summary>Registry built and optimized GLB available.</summary>
    Ready = 2,

    /// <summary>Optimization failed. The raw GLB is still servable; see <see cref="SceneRevision.RegistryReady"/> for replays.</summary>
    Failed = 3,
}

/// <summary>
/// One developer's scene, identified by <c>(DeveloperId, SceneId)</c> where SceneId is Unity's
/// scene-asset GUID. The geometry lives in <see cref="SceneRevision"/> rows, one per scene hash,
/// because a session must replay against the exact revision it was recorded in (Unity Spec 5.1).
/// </summary>
public sealed class Scene
{
    public required string DeveloperId { get; set; }

    public required string SceneId { get; set; }

    /// <summary>Hash of the most recently uploaded revision; the dashboard's "current" scene.</summary>
    public required string LatestHash { get; set; }

    /// <summary>Dashboard-only display name. Null until renamed; the UI falls back to <see cref="SceneId"/>.</summary>
    public string? DisplayName { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// One immutable uploaded revision of a scene, keyed by its scene hash (Unity Spec 13.2). Files are
/// stored under <c>{ScenesDir}/{developerId}/{sceneId}/{hash}/</c> and never overwritten, so old
/// sessions keep replaying against the geometry they were recorded with.
/// </summary>
public sealed class SceneRevision
{
    public required string DeveloperId { get; set; }

    public required string SceneId { get; set; }

    public required string Hash { get; set; }

    /// <summary>Path relative to ScenesDir of the GLB exactly as uploaded.</summary>
    public required string RawGlbPath { get; set; }

    public long RawSizeBytes { get; set; }

    /// <summary>Path relative to ScenesDir of the optimized GLB, once <see cref="Status"/> is Ready.</summary>
    public string? OptimizedGlbPath { get; set; }

    public long? OptimizedSizeBytes { get; set; }

    /// <summary>Path relative to ScenesDir of the object registry JSON the session compiler reads.</summary>
    public string? RegistryPath { get; set; }

    /// <summary>True once the registry exists. Session compilation waits for this, not for optimization.</summary>
    public bool RegistryReady { get; set; }

    /// <summary>Number of tracked objects (nodes with <c>extras.objectId</c>) in the GLB.</summary>
    public int TrackedObjectCount { get; set; }

    public SceneProcessingStatus Status { get; set; } = SceneProcessingStatus.Pending;

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Size of whichever file the dashboard serves: optimized when Ready, otherwise raw.</summary>
    public long ServableSizeBytes => Status == SceneProcessingStatus.Ready && OptimizedSizeBytes.HasValue
        ? OptimizedSizeBytes.Value
        : RawSizeBytes;
}
