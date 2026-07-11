using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Handshake;
using MoodyBlues.Backend.Logging;
using MoodyBlues.Backend.Scenes;
using MoodyBlues.Backend.Server;

var config = ServerConfig.FromEnvironment();

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://{config.Host}:{config.Port}");

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(new RuntimeLogs(config.LogDir, config.BinaryLogFilename, config.EventLogFilename));
builder.Services.AddSingleton<PendingSessionStore>();
builder.Services.AddSingleton<MoodyBluesServer>();
builder.Services.AddDbContext<MoodyBluesDbContext>(options => options.UseNpgsql(config.DbConnectionString));
builder.Services.AddRequestDecompression();

var app = builder.Build();

using (var migrationScope = app.Services.CreateScope())
{
    var db = migrationScope.ServiceProvider.GetRequiredService<MoodyBluesDbContext>();
    await db.Database.MigrateAsync();
}

app.UseRequestDecompression();
app.UseWebSockets();

HandshakeEndpoints.Map(app);
SceneUploadEndpoints.Map(app);

app.Map("/stream", async (HttpContext context, MoodyBluesServer server, PendingSessionStore sessions) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    string? sessionId = context.Request.Query["session"];
    if (string.IsNullOrEmpty(sessionId) || !sessions.TryTake(sessionId, out var pending))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync("Unknown or expired session -- call POST /handshake first.");
        return;
    }

    var socket = await context.WebSockets.AcceptWebSocketAsync();
    string peer = context.Connection.RemoteIpAddress is { } ip
        ? $"{ip}:{context.Connection.RemotePort}"
        : "unknown";
    ConsoleLog.Info($"/stream: session={sessionId} developer={pending!.DeveloperId} scene={pending.SceneId}");
    await server.HandleConnectionAsync(socket, peer, context.RequestAborted);
});

var runtimeLogs = app.Services.GetRequiredService<RuntimeLogs>();
ConsoleLog.Info($"Runtime logs: binary={runtimeLogs.BinaryLogPath} events={runtimeLogs.EventLogPath}");
ConsoleLog.Info(
    $"MoodyBlues backend listening on http://{config.Host}:{config.Port}/ -- " +
    "POST /handshake, POST /scenes/{sceneId}, and ws://.../stream (Unity event socket, per Spec.md Section 1)");

app.Lifetime.ApplicationStopping.Register(() => ConsoleLog.Info("Shutdown requested -- draining connections..."));

await app.RunAsync();

ConsoleLog.Info("MoodyBlues backend stopped.");
