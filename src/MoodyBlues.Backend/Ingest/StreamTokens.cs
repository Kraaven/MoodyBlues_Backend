using System.Security.Cryptography;

namespace MoodyBlues.Backend.Ingest;

/// <summary>
/// The secret a client presents (in the <c>X-MoodyBlues-Stream-Token</c> header, never the URL) to
/// stream into its session. Only its SHA-256 is stored, so a database leak does not let anyone
/// stream into existing sessions. The token stays valid for the whole session, so the client's
/// reconnects keep working, including across server restarts.
/// </summary>
public static class StreamTokens
{
    public static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
