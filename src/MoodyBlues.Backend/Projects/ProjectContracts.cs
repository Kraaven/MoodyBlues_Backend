using MoodyBlues.Backend.Data;

namespace MoodyBlues.Backend.Projects;

public sealed record CreateProjectRequest(string Name);

public sealed record ProjectSummaryResponse(Guid Id, string Name, string DeveloperId, DateTime CreatedAtUtc, int SceneCount);

/// <summary>
/// <see cref="SizeBytes"/> reflects whichever file is currently servable for this scene (the
/// optimized output once <see cref="ProcessingStatus"/> is <c>Ready</c>, otherwise the raw upload) --
/// mirrors the fallback logic in <see cref="MoodyBlues.Backend.Scenes.SceneEndpoints.DownloadAsync"/>,
/// so the dashboard can show a file-size stat without downloading the whole <c>.glb</c>.
/// </summary>
public sealed record ProjectSceneResponse(
    string SceneId,
    string? DisplayName,
    DateTime UpdatedAtUtc,
    long SizeBytes,
    SceneProcessingStatus ProcessingStatus);

public sealed record ProjectDetailResponse(Guid Id, string Name, string DeveloperId, DateTime CreatedAtUtc, IReadOnlyList<ProjectSceneResponse> Scenes);
