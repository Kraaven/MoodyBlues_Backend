using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using MoodyBlues.Backend.Config;

namespace MoodyBlues.Backend.Logging;

/// <summary>
/// Global catch-all for unhandled exceptions. Without this, ASP.NET Core's bare fallback error
/// response drops every header added by earlier middleware -- including the
/// <c>Access-Control-Allow-Origin</c> header <c>UseCors</c> would otherwise have set -- so a
/// server-side bug on a cross-origin request (e.g. the dashboard SPA) surfaces to the browser as a
/// misleading "blocked by CORS policy" error instead of the real 500. This middleware logs the real
/// exception (so it actually shows up in `deploy.sh logs`) and re-applies the CORS header itself
/// before returning a plain JSON error, regardless of what the exception-handling pipeline clears.
/// </summary>
public static class ExceptionHandling
{
    public static void UseGlobalExceptionHandling(this WebApplication app, ServerConfig config)
    {
        app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
        {
            var feature = context.Features.Get<IExceptionHandlerPathFeature>();
            if (feature?.Error is { } exception)
            {
                ConsoleLog.Error($"Unhandled exception: {context.Request.Method} {feature.Path}", exception);
            }

            string? origin = context.Request.Headers.Origin;
            if (origin is not null && config.CorsOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                context.Response.Headers["Access-Control-Allow-Origin"] = origin;
                context.Response.Headers["Vary"] = "Origin";
            }

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"title":"An unexpected error occurred."}""");
        }));
    }
}
