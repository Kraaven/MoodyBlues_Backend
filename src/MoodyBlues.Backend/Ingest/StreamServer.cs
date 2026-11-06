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

        // A WebSocket allows one send at a time; the heartbeat and the close replies share it.
        using var sendLock = new SemaphoreSlim(1, 1);
        using var heartbeatStop = new CancellationTokenSource();
        Task heartbeat = RunHeartbeatAsync(socket, sendLock, heartbeatStop.Token);

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
                    await CloseOutputAsync(socket, sendLock, WebSocketCloseStatus.NormalClosure, "closing");
                    break;
                }

                if (tooLarge)
                {
                    await CloseOutputAsync(socket, sendLock, WebSocketCloseStatus.MessageTooBig, "message too large");
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
                        await CloseOutputAsync(socket, sendLock, WebSocketCloseStatus.ProtocolError, Truncate(ex.Message));
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
            heartbeatStop.Cancel();
            await heartbeat; // never throws; see RunHeartbeatAsync

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

    /// <summary>
    /// Sends a one-byte KeepAlive (the same event the client sends) every
    /// <see cref="ServerConfig.StreamHeartbeatInterval"/> until the connection ends. Without it the
    /// origin side of the connection is silent, and Cloudflare in front of the backend resets
    /// such connections after about 60 s even while the client is streaming. The client discards
    /// whatever the server sends. Never throws.
    /// </summary>
    private async Task RunHeartbeatAsync(WebSocket socket, SemaphoreSlim sendLock, CancellationToken cancellationToken)
    {
        byte[] keepAlive = [(byte)EventType.KeepAlive];
        try
        {
            using var timer = new PeriodicTimer(config.StreamHeartbeatInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await sendLock.WaitAsync(cancellationToken);
                try
                {
                    if (socket.State != WebSocketState.Open)
                    {
                        return;
                    }

                    await socket.SendAsync(keepAlive, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
                }
                finally
                {
                    sendLock.Release();
                }
            }
        }
        catch (OperationCanceledException) { /* connection ended */ }
        catch (WebSocketException) { /* peer vanished; the receive loop reports it */ }
        catch (ObjectDisposedException) { /* connection ended */ }
    }

    /// <summary>Close reply, serialized with the heartbeat's sends.</summary>
    private static async Task CloseOutputAsync(WebSocket socket, SemaphoreSlim sendLock, WebSocketCloseStatus status, string description)
    {
        await sendLock.WaitAsync();
        try
        {
            await socket.CloseOutputAsync(status, description, CancellationToken.None);
        }
        finally
        {
            sendLock.Release();
        }
    }

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
        public void OnLog(byte style, ReadOnlySpan<byte> utf8Text) { }
    }
}
