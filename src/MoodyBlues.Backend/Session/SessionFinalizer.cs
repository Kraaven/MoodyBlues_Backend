using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Logging;
using MoodyBlues.Backend.Protocol;

namespace MoodyBlues.Backend.Session;

/// <summary>
/// Runs once per <c>/stream</c> connection, right after its socket closes (see
/// <see cref="MoodyBlues.Backend.Server.MoodyBluesServer"/>): builds and optimizes the animation
/// from whatever <see cref="SessionRecorder"/> accumulated, gzips it to disk under
/// <see cref="ServerConfig.SessionsDir"/>, and records the result as a <see cref="Data.Session"/>
/// row. Takes an <see cref="IServiceScopeFactory"/> rather than <see cref="MoodyBluesDbContext"/>
/// directly because this class is a singleton (it outlives any one connection) while the DbContext
/// is scoped -- each call opens its own short-lived scope.
/// </summary>
public sealed class SessionFinalizer(IServiceScopeFactory scopeFactory, ServerConfig config)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task FinalizeAsync(string developerId, string sceneId, SessionTimeline timeline, CancellationToken cancellationToken = default)
    {
        if (timeline.RawEventCount == 0)
        {
            // Nothing was ever recorded (e.g. handshake completed but Unity quit before sending
            // anything) -- don't clutter the dashboard with an empty session.
            return;
        }

        using IServiceScope scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MoodyBluesDbContext>();

        var session = new Data.Session
        {
            Id = Guid.NewGuid(),
            DeveloperId = developerId,
            SceneId = sceneId,
            Status = SessionStatus.Processing,
            RawEventCount = timeline.RawEventCount,
            DurationSeconds = timeline.DurationSeconds,
            ObjectCount = timeline.ObjectOrder.Count,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);

        string? animationFullPath = null;
        string? eventsFullPath = null;

        try
        {
            List<RawObjectTimeline> raw = AnimationBuilder.Build(timeline);
            AnimationFileDto animation = AnimationOptimizer.Optimize(timeline.DurationSeconds, raw);
            long keyframeCount = AnimationOptimizer.CountKeyframes(animation);

            string developerDir = Path.Combine(config.SessionsDir, developerId);
            Directory.CreateDirectory(developerDir);
            string relativePath = Path.Combine(developerId, $"{session.Id}.json.gz");
            animationFullPath = Path.Combine(config.SessionsDir, relativePath);

            using (var fileStream = new FileStream(animationFullPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gzip = new GZipStream(fileStream, CompressionLevel.Optimal))
            {
                await JsonSerializer.SerializeAsync(gzip, animation, JsonOptions, cancellationToken);
            }

            long sizeBytes = new FileInfo(animationFullPath).Length;

            string eventsRelativePath = Path.Combine(developerId, $"{session.Id}.events.bin.gz");
            eventsFullPath = Path.Combine(config.SessionsDir, eventsRelativePath);
            using (var fileStream = new FileStream(eventsFullPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gzip = new GZipStream(fileStream, CompressionLevel.Optimal))
            {
                await gzip.WriteAsync(timeline.StrippedEventLog, cancellationToken);
            }

            long eventLogSizeBytes = new FileInfo(eventsFullPath).Length;

            session.AnimationPath = relativePath;
            session.AnimationSizeBytes = sizeBytes;
            session.EventLogPath = eventsRelativePath;
            session.EventLogSizeBytes = eventLogSizeBytes;
            session.KeyframeCount = keyframeCount;
            session.Status = SessionStatus.Ready;
            await db.SaveChangesAsync(cancellationToken);

            ConsoleLog.Info(
                $"Session {session.Id}: finalized (developer={developerId} scene={sceneId}) -- " +
                $"{timeline.RawEventCount} raw events -> {keyframeCount} keyframes, {sizeBytes} bytes animation gzipped, " +
                $"{eventLogSizeBytes} bytes event log gzipped");
        }
        catch (Exception ex)
        {
            TryDelete(animationFullPath);
            TryDelete(eventsFullPath);

            session.Status = SessionStatus.Failed;
            session.ErrorMessage = ex.Message;
            await db.SaveChangesAsync(cancellationToken);
            ConsoleLog.Error($"Session {session.Id}: failed to build/optimize animation", ex);
        }
    }

    private static void TryDelete(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup of partial artifacts.
        }
    }
}
