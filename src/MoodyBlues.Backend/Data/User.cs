namespace MoodyBlues.Backend.Data;

/// <summary>
/// A dashboard account. Registers/logs in via <see cref="MoodyBlues.Backend.Auth.AuthEndpoints"/>
/// and owns zero or more <see cref="Project"/> rows.
/// </summary>
public sealed class User
{
    public required Guid Id { get; set; }

    /// <summary>Stored lowercased -- comparisons/uniqueness are case-insensitive.</summary>
    public required string Email { get; set; }

    /// <summary>PBKDF2 hash, encoded as "{iterations}.{saltBase64}.{hashBase64}" (see <see cref="MoodyBlues.Backend.Auth.PasswordHasher"/>).</summary>
    public required string PasswordHash { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
