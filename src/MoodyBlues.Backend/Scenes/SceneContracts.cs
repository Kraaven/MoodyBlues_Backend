using MoodyBlues.Backend.Data;

namespace MoodyBlues.Backend.Scenes;

public sealed record RenameSceneRequest(string? DisplayName);

/// <summary>
/// A scene as the dashboard lists it: the current (latest uploaded) version's number, hash,
/// size and processing status. <see cref="SizeBytes"/> is the size of the file <c>/file</c> would serve.
/// </summary>
public sealed record SceneResponse(
    string SceneId,
    string? DisplayName,
    DateTime UpdatedAtUtc,
    string LatestHash,
    long SizeBytes,
    SceneProcessingStatus ProcessingStatus,
    int RevisionCount,
    int CurrentVersion);

/// <summary>One stored version of a scene.</summary>
public sealed record SceneVersionResponse(
    int Version,
    string Hash,
    DateTime UploadedAtUtc,
    bool IsCurrent,
    long SizeBytes,
    SceneProcessingStatus ProcessingStatus,
    string? ErrorMessage,
    int SkippedMeshCount,
    int TrackedObjectCount,
    int SessionCount,
    long SessionBytes);

/// <summary>
/// <c>GET /api/scenes/{developerId}/{sceneId}</c>: the scene, its current version, and every
/// archived version, newest first. <see cref="AwaitingSceneSessionCount"/> counts sessions
/// recorded in a version that has not been uploaded yet.
/// </summary>
public sealed record SceneDetailResponse(
    string DeveloperId,
    string SceneId,
    string? DisplayName,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    SceneVersionResponse? Current,
    IReadOnlyList<SceneVersionResponse> Archived,
    int AwaitingSceneSessionCount,
    long TotalBytes);

/// <summary>What a delete removed.</summary>
public sealed record DeleteResponse(int DeletedVersions, int DeletedSessions, long FreedBytes);
