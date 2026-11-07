using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace MoodyBlues.Backend.Common;

/// <summary>
/// Per-client-IP rate limits. Behind the reverse proxy the client IP comes from forwarded headers,
/// which are trusted only when MOODYBLUES_TRUST_FORWARDED_HEADERS is set (the backend must then be
/// reachable only through the proxy, as docker-compose does). With Cloudflare in front, the
/// right-most X-Forwarded-For entry is a Cloudflare edge address shared by many clients, so the
/// <c>CF-Connecting-IP</c> header Cloudflare sets is preferred when present.
/// </summary>
public static class RateLimits
{
    /// <summary>Login and registration: slows credential stuffing (PBKDF2 is deliberately expensive).</summary>
    public const string Auth = "auth";

    /// <summary>Unity-facing endpoints: handshake, stream connect, scene upload.</summary>
    public const string Ingest = "ingest";

    private const string CloudflareClientIpHeader = "CF-Connecting-IP";

    public static IServiceCollection AddMoodyBluesRateLimits(this IServiceCollection services, bool trustForwardedHeaders)
    {
        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(Auth, context => RateLimitPartition.GetFixedWindowLimiter(
                ClientKey(context, trustForwardedHeaders),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

            options.AddPolicy(Ingest, context => RateLimitPartition.GetFixedWindowLimiter(
                ClientKey(context, trustForwardedHeaders),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }

    internal static string ClientKey(HttpContext context, bool trustForwardedHeaders)
    {
        if (trustForwardedHeaders &&
            context.Request.Headers.TryGetValue(CloudflareClientIpHeader, out var cf) &&
            System.Net.IPAddress.TryParse(cf.ToString(), out var cfAddress))
        {
            return cfAddress.ToString();
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
