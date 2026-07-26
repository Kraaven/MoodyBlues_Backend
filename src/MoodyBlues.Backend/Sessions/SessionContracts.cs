using MoodyBlues.Backend.Data;

namespace MoodyBlues.Backend.Sessions;

public sealed record SessionSummaryResponse(
    Guid Id,
    string SceneId,
    DateTime CreatedAtUtc,
    double DurationSeconds,
    long RawEventCount,
    long KeyframeCount,
    int ObjectCount,
    long AnimationSizeBytes,
    SessionStatus Status);

public sealed record SessionDetailResponse(
    Guid Id,
    string DeveloperId,
    string SceneId,
    DateTime CreatedAtUtc,
    double DurationSeconds,
    long RawEventCount,
    long KeyframeCount,
    int ObjectCount,
    long AnimationSizeBytes,
    SessionStatus Status,
    string? ErrorMessage);
