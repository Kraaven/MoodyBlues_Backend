namespace MoodyBlues.Backend.Data;

/// <summary>
/// A developer account. Stubbed for now -- there is no registration/auth
/// endpoint yet, so rows are auto-provisioned the first time a
/// <c>DeveloperId</c> is seen in a handshake (see
/// <see cref="MoodyBlues.Backend.Handshake.HandshakeEndpoints"/>).
/// </summary>
public sealed class Developer
{
    public required string Id { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
