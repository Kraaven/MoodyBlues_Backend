namespace MoodyBlues.Backend.Projects;

public sealed record CreateProjectRequest(string Name);

public sealed record ProjectSummaryResponse(Guid Id, string Name, string DeveloperId, DateTime CreatedAtUtc, int SceneCount);

public sealed record ProjectSceneResponse(string SceneId, string? DisplayName, DateTime UpdatedAtUtc);

public sealed record ProjectDetailResponse(Guid Id, string Name, string DeveloperId, DateTime CreatedAtUtc, IReadOnlyList<ProjectSceneResponse> Scenes);
