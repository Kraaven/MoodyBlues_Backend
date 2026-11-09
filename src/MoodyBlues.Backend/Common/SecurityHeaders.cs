using Microsoft.AspNetCore.Builder;
using MoodyBlues.Backend.Config;

namespace MoodyBlues.Backend.Common;

/// <summary>
/// Response headers for every API response (security plan phase 3). The API serves JSON and binary
/// files, never pages, so its Content-Security-Policy forbids everything: a response opened
/// directly in a browser cannot run script or be framed. The dashboard's own CSP is set by its
/// static host (MoodyBlues_FrontEnd/vercel.json).
/// </summary>
public static class SecurityHeaders
{
    public static void UseSecurityHeaders(this WebApplication app, ServerConfig config)
    {
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            if (config.IsHttps)
            {
                headers["Strict-Transport-Security"] = "max-age=31536000";
            }

            await next();
        });
    }
}
