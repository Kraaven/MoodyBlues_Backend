using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Logging;
using MoodyBlues.Backend.Protocol;
using MoodyBlues.Backend.Storage;

namespace MoodyBlues.Backend.Ingest;

/// <summary>
/// <c>GET /stream?token=...</c>: the Unity client's event WebSocket (Unity Spec 2, 8).
///
/// The receive loop does as little as possible: it checks each message decodes (so garbage and
/// wrong protocol versions are refused at the door), appends the raw bytes to the session's
/// raw log, and re-arms the inactivity timer. No replay state is built here; that happens later
/// in the compile worker, so memory per connection is constant and a slow compile can never
/// back up a live connection.
/// </summary>
public sealed class StreamServer(
    ServerConfig config,
    StoragePaths paths,
    RawLogWriterRegistry writers,
    ActiveConnections connections,
    CompileWorkSignal compileSignal,
    IServiceScopeFactory scopeFactory)
{
    private static readonly TimeSpan ActivityWriteInterval = TimeSpan.FromSeconds(10);

    public static void Map(WebApplication app)
    {
        app.Map("/stream", async (HttpContext context, StreamServer server) => await server.HandleAsync(context))
            .RequireRateLimiting(RateLimits.Ingest);
    }

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        string? token = context.Request.Query["token"];
        if (string.IsNullOrEmpty(token) || token.Length > 128)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        Session? session;
        using (IServiceScope scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MoodyBluesDbContext>();
            string hash = StreamTokens.Hash(token);
            session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.StreamTokenHash == hash);
        }

        if (session is null || session.Status != SessionStatus.Recording)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unknown or ended session; call POST /handshake first.");
            return;
        }

        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
        await RunConnectionAsync(socket, session.Id, session.RawLogPath, context.RequestAborted);
    }

    private async Task RunConnectionAsync(WebSocket socket, Guid sessionId, string rawLogRelative, CancellationToken requestAborted)
    {
        string rawLogFull = paths.SessionFull(rawLogRelative);
        RawLogWriter writer = writers.Acquire(rawLogFull);
        connections.Opened(sessionId);

        int connectionIndex = await RecordConnectionOpenedAsync(sessionId);
        writer.AppendConnectionOpened((uint)connectionIndex);

        var decoder = new WireDecoder(requireBeginSession: true);
        var endDetector = new EndSessionDetector();
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        inactivity.CancelAfter(config.StreamInactivityTimeout);

        var closeReason = RawLogCloseReason.ClientClosed;
        long messages = 0, rejected = 0;
        DateTime lastActivityWrite = DateTime.UtcNow;

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                bool tooLarge = false;
                do
                {
                    result = await socket.ReceiveAsync(buffer, inactivity.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (message.Length + result.Count > config.MaxStreamMessageBytes)
                    {
                        tooLarge = true;
                    }
                    else
                    {
                        message.Write(buffer, 0, result.Count);
                    }
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
                    break;
                }

                if (tooLarge)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "message too large", CancellationToken.None);
                    closeReason = RawLogCloseReason.Error;
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Binary)
                {
                    continue; // the protocol is binary-only; ignore anything else
                }

                var bytes = new ReadOnlyMemory<byte>(message.GetBuffer(), 0, (int)message.Length);
                try
                {
                    decoder.DecodeMessage(bytes.Span, endDetector);
                }
                catch (ProtocolException ex)
                {
                    rejected++;
                    if (!decoder.HasBegunSession)
                    {
                        // Wrong protocol version or not a MoodyBlues client: refuse the connection.
                        ConsoleLog.Warn($"Session {sessionId}: refusing connection: {ex.Message}");
                        await socket.CloseOutputAsync(WebSocketCloseStatus.ProtocolError, Truncate(ex.Message), CancellationToken.None);
                        closeReason = RawLogCloseReason.Error;
                        break;
                    }

                    ConsoleLog.Warn($"Session {sessionId}: rejected a {bytes.Length}-byte message: {ex.Message}");
                    writer.Append(RawLogRecordKind.RejectedMessage, bytes.Span);
                    continue;
                }

                writer.Append(RawLogRecordKind.Message, bytes.Span);
                messages++;
                inactivity.CancelAfter(config.StreamInactivityTimeout);

                if (DateTime.UtcNow - lastActivityWrite > ActivityWriteInterval)
                {
                    lastActivityWrite = DateTime.UtcNow;
                    await TouchAsync(sessionId, writer.Length, endedCleanly: false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            closeReason = requestAborted.IsCancellationRequested ? RawLogCloseReason.ServerShutdown : RawLogCloseReason.InactivityTimeout;
        }
        catch (WebSocketException)
        {
            closeReason = RawLogCloseReason.Error; // peer vanished
        }
        catch (Exception ex)
        {
            closeReason = RawLogCloseReason.Error;
            ConsoleLog.Error($"Session {sessionId}: connection failed.", ex);
        }
        finally
        {
            writer.AppendConnectionClosed(closeReason);
            long size = writer.Length;
            writers.Release(rawLogFull);
            connections.Closed(sessionId);

            await TouchAsync(sessionId, size, endDetector.Seen);
            if (endDetector.Seen)
            {
                compileSignal.Signal.Notify();
            }

            ConsoleLog.Info(
                $"Session {sessionId}: connection #{connectionIndex} closed ({closeReason}), " +
                $"{messages} message(s) stored, {rejected} rejected, endSession={endDetector.Seen}.");
        }
    }

    private async Task<int> RecordConnectionOpenedAsync(Guid sessionId)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MoodyBluesDbContext>();
        Session? session = await db.Sessions.FindAsync(sessionId);
        if (session is null)
        {
            return 0;
        }

        int index = session.ConnectionCount;
        session.ConnectionCount++;
        session.LastActivityUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return index;
    }

    /// <summary>Updates activity and size; on a clean end, queues the session for compilation.</summary>
    private async Task TouchAsync(Guid sessionId, long rawLogSize, bool endedCleanly)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MoodyBluesDbContext>();
            Session? session = await db.Sessions.FindAsync(sessionId);
            if (session is null)
            {
                return;
            }

            session.LastActivityUtc = DateTime.UtcNow;
            session.RawLogSizeBytes = rawLogSize;
            if (endedCleanly && session.Status == SessionStatus.Recording && !connections.IsOpen(sessionId))
            {
                session.EndedCleanly = true;
                session.Status = SessionStatus.Queued;
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            ConsoleLog.Error($"Session {sessionId}: could not update session row.", ex);
        }
    }

    private static string Truncate(string s) => s.Length <= 120 ? s : s[..120];

    /// <summary>Notices EndSession during validation-decode without keeping any other state.</summary>
    private sealed class EndSessionDetector : IWireEventSink
    {
        public bool Seen { get; private set; }

        public void OnEndSession(in EndSessionInfo info) => Seen = true;

        public void OnTimeStamp(double seconds) { }
        public void OnBeginSession(in BeginSessionInfo info) { }
        public void OnKeepAlive() { }
        public void OnShow(ushort objectId) { }
        public void OnHide(ushort objectId) { }
        public void OnDelete(ushort objectId) { }
        public void OnInstantiate(ushort objectId, ushort templateObjectId, ushort parentObjectId, System.Numerics.Vector3 position, System.Numerics.Quaternion rotation, System.Numerics.Vector3 scale) { }
        public void OnReparent(ushort objectId, ushort newParentObjectId, System.Numerics.Vector3 position, System.Numerics.Quaternion rotation, System.Numerics.Vector3 scale) { }
        public void OnTransform(ushort objectId, in TransformUpdate update) { }
    }
}
