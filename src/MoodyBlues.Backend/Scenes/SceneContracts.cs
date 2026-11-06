using MoodyBlues.Backend.Data;

namespace MoodyBlues.Backend.Scenes;

public sealed record RenameSceneRequest(string? DisplayName);

/// <summary>
/// A scene as the dashboard sees it: the latest uploaded revision's hash, size and processing
/// status. <see cref="SizeBytes"/> is the size of the file <c>/file</c> would serve.
/// </summary>
public sealed record SceneResponse(
    string SceneId,
    string? DisplayName,
    DateTime UpdatedAtUtc,
    string LatestHash,
    long SizeBytes,
    SceneProcessingStatus ProcessingStatus,
    int RevisionCount);
