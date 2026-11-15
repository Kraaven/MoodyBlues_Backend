using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace MoodyBlues.Backend.Sessions;

/// <summary>Secrets in public share links: 32 random bytes as base64url (43 characters, no padding).</summary>
public static class ShareTokens
{
    private const int Bytes = 32;
    private const int Length = 43;

    public static string Create() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(Bytes));

    /// <summary>Cheap format check so malformed input never reaches the database.</summary>
    public static bool IsWellFormed(string? token)
    {
        if (token is null || token.Length != Length)
        {
            return false;
        }

        foreach (char c in token)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}
