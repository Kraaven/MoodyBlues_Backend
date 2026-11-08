using MoodyBlues.Backend.Data;

namespace MoodyBlues.Backend.Sessions;

public sealed record SessionSummaryResponse(
    Guid Id,
    string SceneId,
    string SceneHash,
    DateTime CreatedAtUtc,
    SessionStatus Status,
    bool EndedCleanly,
    double DurationSeconds,
    int ObjectCount,
    int SpawnCount,
    long KeyframeCount,
    long ReplaySizeBytes,
    long RawLogSizeBytes,
    /// <summary>The scene version the session was recorded in, or null while that version has not been uploaded.</summary>
    int? SceneVersion,
    string? ErrorMessage,
    int InfoLogCount,
    int WarningLogCount,
    int ErrorLogCount);

public sealed record SessionDetailResponse(
    Guid Id,
    string DeveloperId,
    string SceneId,
    string SceneHash,
    DateTime CreatedAtUtc,
    DateTime LastActivityUtc,
    SessionStatus Status,
    bool EndedCleanly,
    int ConnectionCount,
    double DurationSeconds,
    int ObjectCount,
    int SpawnCount,
    long RawEventCount,
    long KeyframeCount,
    long ReplaySizeBytes,
    long RawLogSizeBytes,
    long DeltaPromotions,
    long HardClamps,
    long DroppedEvents,
    int CompilerVersion,
    DateTime? CompiledAtUtc,
    string? ErrorMessage,
    int InfoLogCount,
    int WarningLogCount,
    int ErrorLogCount);
