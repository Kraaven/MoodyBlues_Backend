using System.Security.Cryptography;

namespace MoodyBlues.Backend.Auth;

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashing, dependency-free. The encoded format is
/// <c>{iterations}.{saltBase64}.{hashBase64}</c>, so the iteration count travels with the hash:
/// hashes made with fewer than <see cref="Iterations"/> still verify, and
/// <see cref="NeedsRehash"/> tells the login to replace them (security plan phase 3).
/// </summary>
public static class PasswordHasher
{
    /// <summary>OWASP's 2023 recommendation for PBKDF2-HMAC-SHA256.</summary>
    public const int Iterations = 600_000;

    private const int MinStoredIterations = 1_000;
    private const int MaxStoredIterations = 10_000_000;
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;

    /// <summary>A real hash of a random password, verified against when the email is unknown so that takes as long as a wrong password.</summary>
    private static readonly string DummyHash = Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)));

    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSizeBytes);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    /// <summary>False for a wrong password and for a malformed stored hash; never throws.</summary>
    public static bool Verify(string password, string encodedHash)
    {
        string[] parts = encodedHash.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out int iterations) ||
            iterations < MinStoredIterations || iterations > MaxStoredIterations)
        {
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(parts[1]);
            byte[] expectedHash = Convert.FromBase64String(parts[2]);
            if (expectedHash.Length == 0) return false;
            byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expectedHash.Length);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Spends the same time as verifying a real password, for an account that does not exist.</summary>
    public static void VerifyDummy(string password) => Verify(password, DummyHash);

    /// <summary>True when a stored hash uses fewer iterations than new hashes do.</summary>
    public static bool NeedsRehash(string encodedHash) =>
        !int.TryParse(encodedHash.Split('.')[0], out int iterations) || iterations < Iterations;
}
