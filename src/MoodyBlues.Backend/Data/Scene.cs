namespace MoodyBlues.Backend.Data;

/// <summary>
/// Metadata for one developer's scene. Identity is the composite
/// <c>(DeveloperId, SceneId)</c> so two developers can each own their own
/// <c>SceneId</c> independently.
///
/// <see cref="Hash"/> is stored and compared as an opaque string -- the
/// backend does not parse the uploaded <c>.glb</c> or independently
/// recompute the hash this milestone (see Handshake plan notes).
/// </summary>
public sealed class Scene
{
    public required string DeveloperId { get; set; }

    public required string SceneId { get; set; }

    public required string Hash { get; set; }

    /// <summary>Path (relative to <see cref="Config.ServerConfig.ScenesDir"/>) of the last uploaded .glb.</summary>
    public required string GlbPath { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>
    /// Dashboard-only display name set via <c>PATCH /api/scenes/{developerId}/{sceneId}</c> (see
    /// <see cref="MoodyBlues.Backend.Scenes.SceneEndpoints"/>). Purely cosmetic -- <see cref="SceneId"/>
    /// (the name Unity/the wire protocol actually use) never changes. Null until renamed at least once,
    /// in which case the frontend falls back to showing <see cref="SceneId"/>.
    /// </summary>
    public string? DisplayName { get; set; }
}
