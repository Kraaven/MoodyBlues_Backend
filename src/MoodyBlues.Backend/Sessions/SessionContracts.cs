using System.Text.Json;
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
    string? ShareToken,
    /// <summary>This link's viewer settings, an opaque JSON object the frontend owns; null until set.</summary>
    JsonElement? ShareSettings);

/// <summary>
/// The session as an anonymous visitor sees it: <see cref="SessionDetailResponse"/> without the internal
/// project id, the public flag and the share token.
/// </summary>
public sealed record SharedSessionDetail(
    Guid Id,
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
    SessionActivity? Activity);

public sealed record SharingRequest(bool IsPublic);

public sealed record SharingResponse(bool IsPublic, string? ShareToken);

/// <summary>Body of the settings PUTs: a JSON object, or null to clear.</summary>
public sealed record ShareSettingsRequest(JsonElement? Settings);

public sealed record ShareSettingsResponse(JsonElement? Settings);

/// <summary>
/// What the anonymous share page needs: the session plus the names it displays, and the viewer
/// settings in force (the link's own, else the project's defaults, else null for the built-in default).
/// </summary>
public sealed record SharedSessionResponse(
    SharedSessionDetail Session, string ProjectName, string SceneName, int? SceneVersion, JsonElement? Settings);
