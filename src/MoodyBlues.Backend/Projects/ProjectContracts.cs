using System.Text.Json;
using MoodyBlues.Backend.Scenes;

namespace MoodyBlues.Backend.Projects;

public sealed record CreateProjectRequest(string Name);

/// <summary>
/// A project in the dashboard's list. <see cref="IngestKeyPrefix"/> identifies the active ingest
/// key. The connection string is only in the detail response.
/// </summary>
public sealed record ProjectSummaryResponse(
    Guid Id,
    string Name,
    DateTime CreatedAtUtc,
    int SceneCount,
    string IngestKeyPrefix,
    DateTime IngestKeyCreatedAtUtc);

/// <summary>
/// One project. <see cref="ConnectionString"/> is what the user pastes into Unity
/// (<c>https://mbk_...@host</c>); null only when the stored key cannot be decrypted (the server's
/// JWT secret changed), in which case rotating the key shows a new one.
/// </summary>
public sealed record ProjectDetailResponse(
    Guid Id,
    string Name,
    DateTime CreatedAtUtc,
    IReadOnlyList<SceneResponse> Scenes,
    string IngestKeyPrefix,
    DateTime IngestKeyCreatedAtUtc,
    string? ConnectionString,
    /// <summary>The template new share links copy, an opaque JSON object the frontend owns; null = the viewer's built-in default.</summary>
    JsonElement? ShareDefaults);

/// <summary>A newly created ingest key. The previous key stops working immediately.</summary>
public sealed record IngestKeyResponse(string ConnectionString, string IngestKeyPrefix, DateTime IngestKeyCreatedAtUtc);
