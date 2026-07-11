using System.Net.WebSockets;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Logging;
using MoodyBlues.Backend.Protocol;

namespace MoodyBlues.Backend.Server;

/// <summary>
/// Decodes the Unity binary event stream for one already-accepted WebSocket
/// connection at a time (see the <c>/stream</c> minimal API route in
/// Program.cs, which does the HTTP-to-WebSocket upgrade and session-token
/// validation before handing the socket off here).
///
/// For this milestone the server has no persistent object store yet -- it
/// just decodes every event per Spec.md and logs everything in detail
/// (console + the two runtime log files) so the wire protocol can be
/// validated end-to-end against the real Unity client
/// (<c>BluesStreamer.ConnectAsync</c>, which connects to us).
/// </summary>
public sealed class MoodyBluesServer(ServerConfig config, RuntimeLogs runtimeLogs)
{
    private int _nextConnectionId;

    /// <summary>Owns one WebSocket connection end-to-end: receive loop, decode, log, summary. Disposes <paramref name="socket"/>.</summary>
    public async Task HandleConnectionAsync(WebSocket socket, string peer, CancellationToken cancellationToken)
    {
        int connectionId = Interlocked.Increment(ref _nextConnectionId);
        ConsoleLog.Info($"Connection #{connectionId} opened from {peer}");

        var stats = new ConnectionStats();
        var receiveBuffer = new byte[8192];
        using var messageStream = new MemoryStream();

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                byte[]? message = await ReceiveMessageAsync(socket, messageStream, receiveBuffer, connectionId, cancellationToken);
                if (message is null)
                {
                    break;
                }

                stats.MessageCount++;
                ProcessMessage(message, connectionId, stats.MessageCount, stats);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
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
            socket.Dispose();
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

    /// <summary>Decodes one binary message, writes it to the runtime logs, and updates connection stats.</summary>
    private void ProcessMessage(byte[] message, int connectionId, int messageNumber, ConnectionStats stats)
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

        List<MoodyEvent> decodedEvents;
        try
        {
            decodedEvents = EventParser.ParseMessage(message);
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
