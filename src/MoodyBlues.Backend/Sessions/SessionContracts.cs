using MoodyBlues.Backend.Compiler;
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
    int ErrorLogCount,
    /// <summary>The session's activity glyph; null until the session is Ready.</summary>
    SessionActivity? Activity,
    bool IsPublic,
    string? ShareToken);

public sealed record SessionDetailResponse(
    Guid Id,
    Guid ProjectId,
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
    long KeyframeCount,
    long ReplaySizeBytes,
    long RawLogSizeBytes,
    long DeltaPromotions,
    long HardClamps,
    long DroppedEvents,
    long RotationWidenings,
    int CompilerVersion,
    DateTime? CompiledAtUtc,
    string? ErrorMessage,
    int InfoLogCount,
    int WarningLogCount,
    int ErrorLogCount,
    SessionActivity? Activity,
    bool IsPublic,
    string? ShareToken);

public sealed record SharingRequest(bool IsPublic);

public sealed record SharingResponse(bool IsPublic, string? ShareToken);

/// <summary>What the anonymous share page needs: the session plus the names it displays.</summary>
public sealed record SharedSessionResponse(SessionDetailResponse Session, string ProjectName, string SceneName, int? SceneVersion);
