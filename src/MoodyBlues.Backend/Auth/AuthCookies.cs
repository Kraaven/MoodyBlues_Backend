using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using MoodyBlues.Backend.Config;

namespace MoodyBlues.Backend.Auth;

/// <summary>
/// The dashboard's two httpOnly cookies (security plan phase 3), so no token is ever readable by
/// page scripts:
/// - <see cref="AccessCookie"/>: the short-lived JWT, sent with every request;
/// - <see cref="RefreshCookie"/>: the long-lived refresh token, sent only to <c>/auth</c>.
///
/// Cross-site request forgery is blocked by <see cref="CsrfProtection"/> (a custom header on every
/// state-changing request, which only allowed origins can send) on top of SameSite.
/// </summary>
public static class AuthCookies
{
    public const string AccessCookie = "mb_access";
    public const string RefreshCookie = "mb_refresh";
    public const string RefreshPath = "/auth";

    public static void Set(HttpResponse response, ServerConfig config, string accessToken, string refreshToken, DateTime refreshExpiresUtc)
    {
        response.Cookies.Append(AccessCookie, accessToken, Options(config, "/", DateTimeOffset.UtcNow.Add(config.AccessTokenLifetime)));
        response.Cookies.Append(RefreshCookie, refreshToken, Options(config, RefreshPath, refreshExpiresUtc));
    }

    public static void Clear(HttpResponse response, ServerConfig config)
    {
        response.Cookies.Delete(AccessCookie, Options(config, "/", null));
        response.Cookies.Delete(RefreshCookie, Options(config, RefreshPath, null));
    }

    private static CookieOptions Options(ServerConfig config, string path, DateTimeOffset? expires)
    {
        SameSiteMode sameSite = config.CookieSameSite.ToLowerInvariant() switch
        {
            "none" => SameSiteMode.None,
            "strict" => SameSiteMode.Strict,
            _ => SameSiteMode.Lax,
        };

        return new CookieOptions
        {
            HttpOnly = true,
            // Browsers reject SameSite=None without Secure; plain-HTTP development uses Lax instead.
            Secure = config.CookieSecure,
            SameSite = sameSite == SameSiteMode.None && !config.CookieSecure ? SameSiteMode.Lax : sameSite,
            Path = path,
            Expires = expires,
            IsEssential = true,
        };
    }
}

/// <summary>
/// Requires <see cref="HeaderName"/> on every state-changing request to <c>/api</c> and <c>/auth</c>.
/// A cross-site form or script cannot add a custom header without a CORS preflight, and the CORS
/// policy only allows the dashboard's origins, so a forged request never carries it.
/// </summary>
public static class CsrfProtection
{
    public const string HeaderName = "X-MoodyBlues-Client";

    public static void UseCsrfProtection(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            string method = context.Request.Method;
            bool unsafeMethod = !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method));
            PathString path = context.Request.Path;
            bool dashboardRoute = path.StartsWithSegments("/api") || path.StartsWithSegments("/auth");

            if (unsafeMethod && dashboardRoute && string.IsNullOrEmpty(context.Request.Headers[HeaderName]))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync($"Missing the {HeaderName} header.");
                return;
            }

            await next();
        });
    }
}
