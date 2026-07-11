namespace MoodyBlues.Backend.Common;

/// <summary>Validates untrusted identifiers (developerId, sceneId) before they're used as URL/filesystem path segments.</summary>
public static class PathSegments
{
    private const int MaxLength = 200;

    public static bool IsSafe(string value) =>
        value.Length is > 0 and <= MaxLength &&
        !value.Contains('/') &&
        !value.Contains('\\') &&
        !value.Contains("..", StringComparison.Ordinal);
}
