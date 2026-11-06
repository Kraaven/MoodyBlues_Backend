using System.Security.Cryptography;
using System.Text;

namespace MoodyBlues.Backend.Ingest;

/// <summary>
/// The secret in a session's WebSocket URL. Only its SHA-256 is stored, so a database leak does
/// not let anyone stream into existing sessions. The token stays valid for the whole session, so
/// the client's reconnects (which reuse the URL) keep working, including across server restarts.
/// </summary>
public static class StreamTokens
{
    public static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
