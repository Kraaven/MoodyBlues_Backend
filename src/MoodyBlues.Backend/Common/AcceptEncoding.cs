using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace MoodyBlues.Backend.Common;

/// <summary>Reads the request's <c>Accept-Encoding</c> for routes that serve pre-compressed files.</summary>
public static class AcceptEncoding
{
    /// <summary>True when <paramref name="coding"/> (e.g. "gzip", "br") is listed with a non-zero quality.</summary>
    public static bool Accepts(HttpRequest request, string coding) =>
        request.GetTypedHeaders().AcceptEncoding.Any(e =>
            string.Equals(e.Value.Value, coding, StringComparison.OrdinalIgnoreCase) && e.Quality != 0);
}
