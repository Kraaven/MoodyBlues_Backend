namespace MoodyBlues.Backend.Data;

/// <summary>
/// The owner identity behind a Unity client's <c>developerId</c>. Created when a dashboard
/// project is created (<see cref="Project.DeveloperId"/>), or on first handshake.
/// </summary>
public sealed class Developer
{
    public required string Id { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
