using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MoodyBlues.Backend.Auth;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Compiler;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Ingest;
using MoodyBlues.Backend.Logging;
using MoodyBlues.Backend.Projects;
using MoodyBlues.Backend.Scenes;
using MoodyBlues.Backend.Scenes.Processing;
using MoodyBlues.Backend.Sessions;
using MoodyBlues.Backend.Storage;

var config = ServerConfig.FromEnvironment();

if (config.JwtSecret == ServerConfig.InsecureDefaultJwtSecret)
{
    ConsoleLog.Warn("MOODYBLUES_JWT_SECRET is not set; using the insecure development default. Set it before deploying.");
}
else if (System.Text.Encoding.UTF8.GetByteCount(config.JwtSecret) < 32)
{
    throw new InvalidOperationException("MOODYBLUES_JWT_SECRET must be at least 32 bytes long.");
}

if (config.CorsOrigins.Contains("*"))
{
    throw new InvalidOperationException("MOODYBLUES_CORS_ORIGIN cannot be '*': the dashboard sends credentials. List the dashboard's origins.");
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://{config.Host}:{config.Port}");

builder.Services.AddSingleton(config);
builder.Services.AddSingleton<StoragePaths>();
builder.Services.AddSingleton<RawLogWriterRegistry>();
builder.Services.AddSingleton<ActiveConnections>();
builder.Services.AddSingleton<SceneWorkSignal>();
builder.Services.AddSingleton<CompileWorkSignal>();
builder.Services.AddSingleton<StreamServer>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<IngestKeyProtector>();
builder.Services.AddScoped<IGltfOptimizer, GltfTransformOptimizer>();
builder.Services.AddHostedService<SceneProcessingWorker>();
builder.Services.AddHostedService<CompileWorker>();
builder.Services.AddDbContext<MoodyBluesDbContext>(options => options.UseNpgsql(config.DbConnectionString));
builder.Services.AddRequestDecompression();

bool trustForwardedHeaders = Environment.GetEnvironmentVariable("MOODYBLUES_TRUST_FORWARDED_HEADERS") is "1" or "true" or "True";
builder.Services.AddMoodyBluesRateLimits(trustForwardedHeaders);

// Enums travel as their names ("Ready", not 3) so the dashboard needs no lookup table.
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddCors(options => options.AddPolicy("dashboard", policy => policy
    .WithOrigins(config.CorsOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    // The dashboard signs in with httpOnly cookies, so its requests carry credentials.
    .AllowCredentials()));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new JwtTokenService(config).BuildValidationParameters();
        options.Events = new JwtBearerEvents
        {
            // The dashboard's access token is in an httpOnly cookie; a Bearer header still wins.
            OnMessageReceived = context =>
            {
                if (string.IsNullOrEmpty(context.Token) && context.Request.Cookies.TryGetValue(AuthCookies.AccessCookie, out string? cookie))
                {
                    context.Token = cookie;
                }

                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization();

if (trustForwardedHeaders)
{
    // Only safe when the backend is reachable solely through the reverse proxy (docker-compose
    // publishes no backend port); otherwise a client could spoof its IP for rate limiting.
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

var app = builder.Build();

using (IServiceScope migrationScope = app.Services.CreateScope())
{
    var db = migrationScope.ServiceProvider.GetRequiredService<MoodyBluesDbContext>();
    await db.Database.MigrateAsync();
}

if (trustForwardedHeaders)
{
    app.UseForwardedHeaders();
}

app.UseGlobalExceptionHandling(config);
app.UseSecurityHeaders(config);
app.UseCors("dashboard");
app.UseCsrfProtection();
app.UseRequestDecompression();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", async (MoodyBluesDbContext db) =>
    await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503));

HandshakeEndpoints.Map(app);
SceneCheckEndpoints.Map(app);
StreamServer.Map(app);
SceneUploadEndpoints.Map(app);
AuthEndpoints.Map(app);
ProjectEndpoints.Map(app);
SceneEndpoints.Map(app);
SessionEndpoints.Map(app);

ConsoleLog.Info(
    $"MoodyBlues backend listening on http://{config.Host}:{config.Port}/ " +
    $"(public {config.PublicScheme}://{config.PublicHostAndPort()}); scenes={Path.GetFullPath(config.ScenesDir)} " +
    $"sessions={Path.GetFullPath(config.SessionsDir)}");

await app.RunAsync();
