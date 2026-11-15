using System.Security.Cryptography;
using System.Text;

namespace MoodyBlues.Backend.Common;

/// <summary>Hashing shared by the secrets the database stores only as hashes (ingest keys, stream tokens).</summary>
public static class Hashing
{
    /// <summary>SHA-256 of the UTF-8 text, as lowercase hex.</summary>
    public static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
