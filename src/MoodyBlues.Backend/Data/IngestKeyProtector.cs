using System.Security.Cryptography;
using System.Text;
using MoodyBlues.Backend.Config;

namespace MoodyBlues.Backend.Data;

/// <summary>
/// Encrypts the ingest key for storage, so the dashboard can show the project's connection string
/// at any time while a copy of the database alone does not reveal it.
///
/// AES-256-GCM, with the key derived (HKDF-SHA256) from <see cref="ServerConfig.JwtSecret"/>, so a
/// deployment needs no extra secret. The project's ID is the associated data, so a ciphertext
/// cannot be moved to another project's row. Changing the JWT secret makes stored keys unreadable:
/// the keys keep working (lookups use their hash), and the dashboard asks the owner to rotate to
/// see a connection string again.
/// </summary>
public sealed class IngestKeyProtector
{
    private const string Version = "v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public IngestKeyProtector(ServerConfig config)
    {
        _key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(config.JwtSecret),
            outputLength: 32,
            salt: null,
            info: Encoding.UTF8.GetBytes("moodyblues ingest key v1"));
    }

    public string Protect(string ingestKey, Guid projectId)
    {
        byte[] plaintext = Encoding.UTF8.GetBytes(ingestKey);
        byte[] output = new byte[NonceSize + plaintext.Length + TagSize];
        Span<byte> nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(
            nonce,
            plaintext,
            output.AsSpan(NonceSize, plaintext.Length),
            output.AsSpan(NonceSize + plaintext.Length, TagSize),
            projectId.ToByteArray());

        return Version + Convert.ToBase64String(output);
    }

    /// <summary>The stored key, or null if there is none or it cannot be decrypted (the JWT secret changed).</summary>
    public string? TryUnprotect(string? stored, Guid projectId)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Version, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            byte[] input = Convert.FromBase64String(stored[Version.Length..]);
            int length = input.Length - NonceSize - TagSize;
            if (length <= 0)
            {
                return null;
            }

            byte[] plaintext = new byte[length];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(
                input.AsSpan(0, NonceSize),
                input.AsSpan(NonceSize, length),
                input.AsSpan(NonceSize + length, TagSize),
                plaintext,
                projectId.ToByteArray());
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }
}
