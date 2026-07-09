using System.Net;
using System.Net.WebSockets;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Logging;
using MoodyBlues.Backend.Protocol;

namespace MoodyBlues.Backend.Server;

/// <summary>
/// HttpListener-based WebSocket server that receives the Unity binary event
/// stream.
///
/// For this milestone the server has no persistent object store yet -- it
/// just accepts connections, decodes every event per Spec.md, and logs
/// everything in detail (console + the two runtime log files) so the wire
/// protocol can be validated end-to-end against the real Unity client
/// (<c>BluesStreamer.ConnectAsync</c>, which connects to us).
/// </summary>
public sealed class MoodyBluesServer(ServerConfig config, RuntimeLogs runtimeLogs)
{
    private int _nextConnectionId;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        string prefix = $"http://{config.Host}:{config.Port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                listener.Stop();
            }
            catch (ObjectDisposedException)
            {
                // already stopped
            }
        });

        ConsoleLog.Info($"Runtime logs: binary={runtimeLogs.BinaryLogPath} events={runtimeLogs.EventLogPath}");
        ConsoleLog.Info(
            $"MoodyBlues backend listening on ws://{config.Host}:{config.Port}/ " +
            "(Unity client connects here, per Spec.md Section 1)");

        var connectionTasks = new List<Task>();

        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }

            int connectionId = Interlocked.Increment(ref _nextConnectionId);
            connectionTasks.Add(HandleConnectionAsync(context, connectionId, cancellationToken));
        }

        await Task.WhenAll(connectionTasks);
    }

    private async Task HandleConnectionAsync(HttpListenerContext context, int connectionId, CancellationToken cancellationToken)
    {
        WebSocketContext wsContext;
        try
        {
            wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
        }
        catch (Exception ex)
        {
            ConsoleLog.Error($"Connection #{connectionId}: WebSocket handshake failed", ex);
            context.Response.StatusCode = 500;
            context.Response.Close();
            return;
        }

        WebSocket socket = wsContext.WebSocket;
        string peer = context.Request.RemoteEndPoint?.ToString() ?? "unknown";
        ConsoleLog.Info($"Connection #{connectionId} opened from {peer}");

        int messageCount = 0;
        int eventCount = 0;
        var eventTypeCounts = new Dictionary<string, int>();

        var receiveBuffer = new byte[8192];
        using var messageStream = new MemoryStream();

        try
        {
            while (socket.State == WebSocketState.Open)
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
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    ConsoleLog.Warn(
                        $"Connection #{connectionId}: ignoring unexpected TEXT message ({messageStream.Length} chars) -- " +
                        "Spec.md says binary only");
                    continue;
                }

                messageCount++;
                byte[] message = messageStream.ToArray();
                int size = message.Length;

                if (config.LogRawBytes)
                {
                    int previewLen = Math.Min(32, size);
                    string preview = Convert.ToHexString(message, 0, previewLen);
                    ConsoleLog.Debug(
                        $"Connection #{connectionId}: message #{messageCount} received, {size} bytes. " +
                        $"First bytes: {preview}{(size > 32 ? " ..." : string.Empty)}");
                }

                List<MoodyEvent>? decodedEvents = null;
                try
                {
                    decodedEvents = EventParser.ParseMessage(message);
                }
                catch (ProtocolException ex)
                {
                    ConsoleLog.Error(
                        $"Connection #{connectionId}: failed to decode message #{messageCount} ({size} bytes): " +
                        Convert.ToHexString(message),
                        ex);
                    runtimeLogs.LogBinaryPacket(connectionId, messageCount, size, null);
                    runtimeLogs.LogDecodeError(connectionId, messageCount, size, ex);
                    continue;
                }

                runtimeLogs.LogBinaryPacket(connectionId, messageCount, size, decodedEvents);
                runtimeLogs.LogDecodedEvents(connectionId, messageCount, size, decodedEvents);

                ConsoleLog.Info($"Connection #{connectionId}: message #{messageCount} -> {size} bytes, {decodedEvents.Count} event(s)");

                foreach (var evt in decodedEvents)
                {
                    eventCount++;
                    string typeName = evt.GetType().Name;
                    eventTypeCounts[typeName] = eventTypeCounts.GetValueOrDefault(typeName) + 1;
                }
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
            string breakdown = string.Join(", ", eventTypeCounts.Select(kv => $"{kv.Key}={kv.Value}"));
            ConsoleLog.Info($"Connection #{connectionId} summary: {messageCount} message(s), {eventCount} event(s) -- breakdown: {{{breakdown}}}");
            socket.Dispose();
        }
    }
}
