using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace MoodyBlues.Backend.Common;

/// <summary>
/// Per-client-IP rate limits. Behind the reverse proxy the client IP comes from
/// <c>X-Forwarded-For</c>, which Program.cs trusts only when MOODYBLUES_TRUST_FORWARDED_HEADERS
/// is set (the backend must then be reachable only through the proxy, as docker-compose does).
/// </summary>
public static class RateLimits
{
    /// <summary>Login and registration: slows credential stuffing (PBKDF2 is deliberately expensive).</summary>
    public const string Auth = "auth";

    /// <summary>Unity-facing endpoints: handshake, stream connect, scene upload.</summary>
    public const string Ingest = "ingest";

    public static IServiceCollection AddMoodyBluesRateLimits(this IServiceCollection services)
    {
        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(Auth, context => RateLimitPartition.GetFixedWindowLimiter(
                ClientKey(context),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

            options.AddPolicy(Ingest, context => RateLimitPartition.GetFixedWindowLimiter(
                ClientKey(context),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }

    private static string ClientKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
