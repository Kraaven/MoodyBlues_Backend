using MoodyBlues.Backend.Scenes;

namespace MoodyBlues.Backend.Projects;

public sealed record CreateProjectRequest(string Name);

/// <summary>
/// A project in the dashboard's list. <see cref="IngestKeyPrefix"/> identifies the active ingest
/// key (null if the project has none yet). The connection string is only in the detail response.
/// </summary>
public sealed record ProjectSummaryResponse(
    Guid Id,
    string Name,
    string DeveloperId,
    DateTime CreatedAtUtc,
    int SceneCount,
    string? IngestKeyPrefix,
    DateTime? IngestKeyCreatedAtUtc);

/// <summary>
/// One project. <see cref="ConnectionString"/> is what the user pastes into Unity
/// (<c>https://mbk_...@host</c>); null when the project has no key, or its key was issued before
/// keys were stored (or the server's secret changed), in which case rotating shows a new one.
/// </summary>
public sealed record ProjectDetailResponse(
    Guid Id,
    string Name,
    string DeveloperId,
    DateTime CreatedAtUtc,
    IReadOnlyList<SceneResponse> Scenes,
    string? IngestKeyPrefix,
    DateTime? IngestKeyCreatedAtUtc,
    string? ConnectionString);

/// <summary>A newly created ingest key. The previous key stops working immediately.</summary>
public sealed record IngestKeyResponse(string ConnectionString, string IngestKeyPrefix, DateTime IngestKeyCreatedAtUtc);
