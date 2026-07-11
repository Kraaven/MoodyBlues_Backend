namespace MoodyBlues.Backend.Data;

/// <summary>
/// A dashboard project belonging to a <see cref="User"/>. <see cref="DeveloperId"/> is the
/// opaque token the user pastes into their Unity client's config -- it's the exact same
/// <c>developerId</c> value <c>POST /handshake</c> and <c>POST /scenes/{sceneId}</c> already
/// key off of, so no changes to the Unity-facing wire protocol are needed.
/// </summary>
public sealed class Project
{
    public required Guid Id { get; set; }

    public required Guid UserId { get; set; }

    public required string Name { get; set; }

    public required string DeveloperId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
