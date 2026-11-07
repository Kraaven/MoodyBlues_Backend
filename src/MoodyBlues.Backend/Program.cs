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
    .AllowAnyMethod()));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => options.TokenValidationParameters = new JwtTokenService(config).BuildValidationParameters());
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
app.UseCors("dashboard");
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
