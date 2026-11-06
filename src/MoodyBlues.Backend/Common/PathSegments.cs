namespace MoodyBlues.Backend.Common;

/// <summary>
/// Validates untrusted identifiers (developerId, sceneId, scene hash) before they are used as
/// URL or filesystem path segments. An allow-list rather than a block-list: only ASCII letters,
/// digits, '-' and '_' are accepted, which rules out separators, "..", drive prefixes ("C:"),
/// null bytes, trailing dots and reserved device-name tricks in one rule.
/// </summary>
public static class PathSegments
{
    private const int MaxLength = 128;

    public static bool IsSafe(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        foreach (char c in value)
        {
            bool ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A scene hash is 64 lowercase hex characters (SHA-256, Unity Spec 13.2).</summary>
    public static bool IsSceneHash(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}
