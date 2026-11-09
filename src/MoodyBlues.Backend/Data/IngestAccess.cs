using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Ingest;

namespace MoodyBlues.Backend.Data;

/// <summary>
/// Who may use the Unity-facing endpoints (<c>/connection</c>, <c>/handshake</c>,
/// <c>/scenes/check</c>, scene upload). The caller sends the project's ingest key in the
/// <see cref="HeaderName"/> header, and the key alone identifies the project: the Unity client
/// never sends a developer ID.
///
/// The key only grants uploads: no dashboard route accepts it. It still ships inside game builds,
/// so the quotas in <see cref="Config.ServerConfig"/> bound what a leaked key can cost, and the
/// dashboard can replace it at any time (<c>POST /api/projects/{id}/ingest-key</c>).
/// </summary>
public static class IngestAccess
{
    public const string HeaderName = "X-MoodyBlues-Ingest-Key";

    /// <summary>Every key starts with this, so a leaked one is recognisable in logs and scanners.</summary>
    public const string KeyPrefix = "mbk_";

    public const string MissingKeyMessage =
        "No connection key was sent. Paste the project's connection string from the MoodyBlues dashboard into MoodyBlues > Settings in Unity.";

    public const string UnknownKeyMessage =
        "This connection key is not valid; it may have been rotated. Copy the current connection string from the project page of the MoodyBlues dashboard into MoodyBlues > Settings in Unity.";

    /// <summary>A new key: the prefix plus 256 random bits, base64url.</summary>
    public static string NewKey() => KeyPrefix + StreamTokens.NewToken();

    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>The part of a key the dashboard shows to identify it: the prefix plus 6 characters.</summary>
    public static string DisplayPrefix(string key) => key[..Math.Min(key.Length, KeyPrefix.Length + 6)];

    /// <summary>
    /// What the user pastes into Unity: the backend's public address with the key as its user
    /// part, e.g. <c>https://mbk_...@moodyblues.example.com</c>. The Unity client splits it into
    /// the base URL and the key, and never sends the URL with the key in it.
    /// </summary>
    public static string ConnectionString(ServerConfig config, string key) =>
        $"{(config.IsHttps ? "https" : "http")}://{key}@{config.PublicHostAndPort()}";

    /// <summary>
    /// The project the request's ingest key belongs to, or the error to return (401) when the key
    /// is missing or matches no project. The lookup is by the key's SHA-256, which is indexed
    /// and unique.
    /// </summary>
    public static async Task<(IngestProject? Project, IResult? Denied)> ResolveAsync(MoodyBluesDbContext db, HttpRequest request)
    {
        string? key = request.Headers[HeaderName];
        if (string.IsNullOrEmpty(key))
        {
            return (null, Results.Json(MissingKeyMessage, statusCode: StatusCodes.Status401Unauthorized));
        }

        if (key.Length > 128 || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            return (null, Results.Json(UnknownKeyMessage, statusCode: StatusCodes.Status401Unauthorized));
        }

        string hash = Hash(key);
        IngestProject? project = await db.Projects
            .Where(p => p.IngestKeyHash == hash)
            .Select(p => new IngestProject(p.DeveloperId, p.Name))
            .FirstOrDefaultAsync();

        return project is null
            ? (null, Results.Json(UnknownKeyMessage, statusCode: StatusCodes.Status401Unauthorized))
            : (project, null);
    }
}

/// <summary>The project an ingest request writes to.</summary>
public sealed record IngestProject(string DeveloperId, string Name);
