namespace MoodyBlues.Backend.Data;

/// <summary>Lifecycle status of a recorded session's server-side animation build.</summary>
public enum SessionStatus
{
    Processing = 0,
    Ready = 1,
    Failed = 2,
}

/// <summary>
/// One finalized recording of a Unity <c>/stream</c> connection: the decoded wire events are
/// turned into a keyframe animation (see <c>Session/AnimationBuilder.cs</c>), optimized (see
/// <c>Session/AnimationOptimizer.cs</c>), and gzipped to disk under
/// <see cref="Config.ServerConfig.SessionsDir"/>, alongside a stripped lifecycle event log
/// (types 24–28 only) for frontend playback cross-checks. This row is the queryable record;
/// the animation and event-log payloads live on disk, not in Postgres.
/// </summary>
public sealed class Session
{
    public required Guid Id { get; set; }

    public required string DeveloperId { get; set; }

    public required string SceneId { get; set; }

    /// <summary>Hash of the scene file this session was recorded against, for staleness detection.</summary>
    public string? SceneHash { get; set; }

    /// <summary>Path (relative to <see cref="Config.ServerConfig.SessionsDir"/>) of the gzipped animation JSON.</summary>
    public string? AnimationPath { get; set; }

    public long AnimationSizeBytes { get; set; }

    /// <summary>Path (relative to <see cref="Config.ServerConfig.SessionsDir"/>) of the gzipped stripped event log (lifecycle wire events).</summary>
    public string? EventLogPath { get; set; }

    public long EventLogSizeBytes { get; set; }

    /// <summary>Number of raw wire events decoded, before any optimization.</summary>
    public long RawEventCount { get; set; }

    /// <summary>Number of keyframes remaining across all channels after optimization.</summary>
    public long KeyframeCount { get; set; }

    public double DurationSeconds { get; set; }

    /// <summary>Number of distinct tracked objects (scene objects + runtime spawns) that appear in the recording.</summary>
    public int ObjectCount { get; set; }

    public SessionStatus Status { get; set; } = SessionStatus.Processing;

    /// <summary>Set when <see cref="Status"/> is <see cref="SessionStatus.Failed"/>, for dashboard/debugging visibility.</summary>
    public string? ErrorMessage { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
