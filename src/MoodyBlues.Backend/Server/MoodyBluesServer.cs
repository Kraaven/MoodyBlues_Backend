using System.Net.WebSockets;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Logging;
using MoodyBlues.Backend.Protocol;
using MoodyBlues.Backend.Scenes.Processing;
using MoodyBlues.Backend.Session;

namespace MoodyBlues.Backend.Server;

/// <summary>
/// Decodes the Unity binary event stream for one already-accepted WebSocket connection at a time
/// (see the <c>/stream</c> minimal API route in Program.cs, which does the HTTP-to-WebSocket
/// upgrade and session-token validation before handing the socket off here). Every decoded event
/// is fed into a per-connection <see cref="SessionRecorder"/>; once the socket closes (cleanly,
/// via inactivity timeout, or on error) <see cref="SessionFinalizer"/> builds, optimizes, and
/// persists whatever was recorded as a <see cref="Data.Session"/>.
/// </summary>
public sealed class MoodyBluesServer(ServerConfig config, RuntimeLogs runtimeLogs, SessionFinalizer sessionFinalizer, SceneObjectRegistryStore registryStore)
{
    private int _nextConnectionId;

    /// <summary>Owns one WebSocket connection end-to-end: receive loop, decode, log, finalize. Disposes <paramref name="socket"/>.</summary>
    public async Task HandleConnectionAsync(WebSocket socket, string peer, string developerId, string sceneId, CancellationToken cancellationToken)
    {
        int connectionId = Interlocked.Increment(ref _nextConnectionId);
        ConsoleLog.Info($"Connection #{connectionId} opened from {peer} (developer={developerId} scene={sceneId})");

        var stats = new ConnectionStats();
        var sceneRegistry = registryStore.Load(developerId, sceneId);
        var recorder = new SessionRecorder(sceneRegistry);
        var receiveBuffer = new byte[8192];
        using var messageStream = new MemoryStream();

        // Linked to the caller's token (app shutdown / HTTP request aborted) so either source can
        // end the connection. CancelAfter is re-armed after every successfully decoded message --
        // see the receive loop below -- so this only actually fires after config.StreamInactivityTimeout
        // of true silence. A clean quit (Unity's BluesSessionManager.StopStreaming) closes the
        // socket itself well before this could ever fire; this is purely the crash/dropped-connection
        // safety net that guarantees a session still gets finalized.
        using var inactivityCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        inactivityCts.CancelAfter(config.StreamInactivityTimeout);

        bool timedOut = false;

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                byte[]? message = await ReceiveMessageAsync(socket, messageStream, receiveBuffer, connectionId, inactivityCts.Token);
                if (message is null)
                {
                    break;
                }

                stats.MessageCount++;
                ProcessMessage(message, connectionId, stats.MessageCount, stats, recorder);

                // Reset the inactivity window -- this connection just proved it's still alive.
                inactivityCts.CancelAfter(config.StreamInactivityTimeout);
            }
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            ConsoleLog.Info(timedOut
                ? $"Connection #{connectionId}: closing after {config.StreamInactivityTimeout.TotalSeconds}s of inactivity"
                : $"Connection #{connectionId}: shutting down");
        }
        catch (WebSocketException ex)
        {
            ConsoleLog.Info($"Connection #{connectionId} closed: {ex.Message}");
        }
        catch (Exception ex)
        {
            ConsoleLog.Error($"Connection #{connectionId}: unhandled error", ex);
        }
        finally
        {
            ConsoleLog.Info($"Connection #{connectionId} summary: {stats.MessageCount} message(s), {stats.EventCount} event(s) -- breakdown: {{{stats.Breakdown()}}}");

            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, timedOut ? "inactivity timeout" : "closing", CancellationToken.None);
                }
                catch
                {
                    // Best-effort -- the socket may already be unusable (e.g. peer vanished mid-close).
                }
            }

            socket.Dispose();

            await sessionFinalizer.FinalizeAsync(developerId, sceneId, recorder.Finalize());
        }
    }

    /// <summary>
    /// Reads one complete WebSocket message (across however many frames it
    /// spans) and returns its bytes, or <c>null</c> if the connection is
    /// closing. Unexpected TEXT messages are logged and skipped.
    /// </summary>
    private static async Task<byte[]?> ReceiveMessageAsync(
        WebSocket socket,
        MemoryStream messageStream,
        byte[] receiveBuffer,
        int connectionId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            messageStream.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                messageStream.Write(receiveBuffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", cancellationToken);
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                ConsoleLog.Warn(
                    $"Connection #{connectionId}: ignoring unexpected TEXT message ({messageStream.Length} chars) -- " +
                    "Spec.md says binary only");
                continue;
            }

            return messageStream.ToArray();
        }
    }

    /// <summary>Decodes one binary message, writes it to the runtime logs, feeds the session recorder, and updates connection stats.</summary>
    private void ProcessMessage(byte[] message, int connectionId, int messageNumber, ConnectionStats stats, SessionRecorder recorder)
    {
        int size = message.Length;

        if (config.LogRawBytes)
        {
            int previewLen = Math.Min(32, size);
            string preview = Convert.ToHexString(message, 0, previewLen);
            ConsoleLog.Debug(
                $"Connection #{connectionId}: message #{messageNumber} received, {size} bytes. " +
                $"First bytes: {preview}{(size > 32 ? " ..." : string.Empty)}");
        }

        List<MoodyEvent> decodedEvents = new();
        try
        {
            // Visit keeps each event's original wire slice so SessionRecorder can copy lifecycle
            // bytes into the stripped event log without a second quantize pass.
            EventParser.VisitMessage(message, (evt, wireBytes) =>
            {
                decodedEvents.Add(evt);
                recorder.Record(evt, wireBytes);
            });
        }
        catch (ProtocolException ex)
        {
            ConsoleLog.Error(
                $"Connection #{connectionId}: failed to decode message #{messageNumber} ({size} bytes): " +
                Convert.ToHexString(message),
                ex);
            runtimeLogs.LogDecodeFailure(connectionId, messageNumber, size, ex);
            return;
        }

        runtimeLogs.LogMessage(connectionId, messageNumber, size, decodedEvents);
        ConsoleLog.Info($"Connection #{connectionId}: message #{messageNumber} -> {size} bytes, {decodedEvents.Count} event(s)");
        stats.AddEvents(decodedEvents);
    }

    /// <summary>Per-connection running totals, used only for the closing summary line.</summary>
    private sealed class ConnectionStats
    {
        private readonly Dictionary<string, int> _eventTypeCounts = new();

        public int MessageCount { get; set; }

        public int EventCount { get; private set; }

        public void AddEvents(IReadOnlyList<MoodyEvent> events)
        {
            foreach (var evt in events)
            {
                EventCount++;
                string typeName = evt.GetType().Name;
                _eventTypeCounts[typeName] = _eventTypeCounts.GetValueOrDefault(typeName) + 1;
            }
        }

        public string Breakdown() => string.Join(", ", _eventTypeCounts.Select(kv => $"{kv.Key}={kv.Value}"));
    }
}
