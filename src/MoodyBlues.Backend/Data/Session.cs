namespace MoodyBlues.Backend.Data;

/// <summary>Lifecycle of a recorded session.</summary>
public enum SessionStatus
{
    /// <summary>A Unity client is (or may still be) streaming. Reconnects append to the same session.</summary>
    Recording = 0,

    /// <summary>Recording is over; waiting for the compile worker (and for the scene registry, if not uploaded yet).</summary>
    Queued = 1,

    /// <summary>The compile worker is turning the raw log into a replay file.</summary>
    Compiling = 2,

    /// <summary>The replay file is available.</summary>
    Ready = 3,

    /// <summary>Compilation failed; see <see cref="Session.ErrorMessage"/>. The raw log is kept, so it can be recompiled.</summary>
    Failed = 4,
}

/// <summary>
/// One play session of a Unity game. Created by <c>POST /handshake</c>, filled by <c>/stream</c>
/// (raw wire bytes appended to <see cref="RawLogPath"/>), then compiled into a replay file
/// (<see cref="ReplayPath"/>) by the compile worker. The raw log is the source of truth: the replay
/// file can always be rebuilt from it.
/// </summary>
public sealed class Session
{
    public required Guid Id { get; set; }

    public required string DeveloperId { get; set; }

    public required string SceneId { get; set; }

    /// <summary>Scene hash the client reported at handshake; selects the <see cref="SceneRevision"/> to replay against.</summary>
    public required string SceneHash { get; set; }

    /// <summary>The Unity client's per-run session GUID. A repeated handshake with the same value reuses this row.</summary>
    public required string ClientSessionId { get; set; }

    /// <summary>SHA-256 (hex) of the secret token in the WebSocket URL. The token itself is never stored.</summary>
    public required string StreamTokenHash { get; set; }

    public SessionStatus Status { get; set; } = SessionStatus.Recording;

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Last time a message arrived. Used to seal sessions whose client vanished without EndSession.</summary>
    public DateTime LastActivityUtc { get; set; }

    /// <summary>True once an EndSession event has been received. False means the recording was truncated.</summary>
    public bool EndedCleanly { get; set; }

    /// <summary>Number of WebSocket connections the client opened (1 + reconnects).</summary>
    public int ConnectionCount { get; set; }

    /// <summary>Path relative to SessionsDir of the append-only raw wire log.</summary>
    public required string RawLogPath { get; set; }

    public long RawLogSizeBytes { get; set; }

    /// <summary>Path relative to SessionsDir of the compiled, gzipped replay file. Null until Ready.</summary>
    public string? ReplayPath { get; set; }

    public long ReplaySizeBytes { get; set; }

    public double DurationSeconds { get; set; }

    public int ObjectCount { get; set; }

    public int SpawnCount { get; set; }

    public long RawEventCount { get; set; }

    public long KeyframeCount { get; set; }

    /// <summary>Client-reported counters from EndSession (Unity Spec 6.1). HardClamps &gt; 0 means some transforms are wrong.</summary>
    public long DeltaPromotions { get; set; }

    public long HardClamps { get; set; }

    public long DroppedEvents { get; set; }

    public int CompilerVersion { get; set; }

    public DateTime? CompiledAtUtc { get; set; }

    public string? ErrorMessage { get; set; }
}
