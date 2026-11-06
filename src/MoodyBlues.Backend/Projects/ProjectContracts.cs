using MoodyBlues.Backend.Scenes;

namespace MoodyBlues.Backend.Projects;

public sealed record CreateProjectRequest(string Name);

public sealed record ProjectSummaryResponse(Guid Id, string Name, string DeveloperId, DateTime CreatedAtUtc, int SceneCount);

public sealed record ProjectDetailResponse(Guid Id, string Name, string DeveloperId, DateTime CreatedAtUtc, IReadOnlyList<SceneResponse> Scenes);
