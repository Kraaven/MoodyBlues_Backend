using System.Globalization;
using System.Text;
using MoodyBlues.Backend.Protocol;

namespace MoodyBlues.Backend.Logging;

/// <summary>
/// Dedicated on-disk logs maintained while the server runs.
///
/// Two separate log files are written under <c>config.LogDir</c> (default
/// <c>logs/</c>):
///
/// - Binary log (<c>binary.log</c>): one line per WebSocket binary message
///   received -- when it arrived, how big it was, and the <c>Seconds</c>
///   value from its <see cref="TimeStampEvent"/> (Spec.md Section 5.1), i.e.
///   the point in Unity's clock the message represents.
/// - Event log (<c>events.log</c>): every event decoded from every message,
///   formatted for readability, with a long separator line between each
///   message's block of events.
///
/// These are independent of the interactive console output -- they exist
/// purely as a persistent, structured record of what the backend has seen.
/// </summary>
public sealed class RuntimeLogs : IDisposable
{
    private const string Separator = "----------------------------------------------------------------------------------------------------";

    private readonly object _binaryLock = new();
    private readonly object _eventLock = new();
    private readonly StreamWriter _binaryWriter;
    private readonly StreamWriter _eventWriter;

    public string BinaryLogPath { get; }
    public string EventLogPath { get; }

    public RuntimeLogs(string logDir, string binaryFilename, string eventFilename)
    {
        Directory.CreateDirectory(logDir);

        BinaryLogPath = Path.Combine(logDir, binaryFilename);
        EventLogPath = Path.Combine(logDir, eventFilename);

        _binaryWriter = new StreamWriter(new FileStream(BinaryLogPath, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8)
        {
            AutoFlush = true,
        };
        _eventWriter = new StreamWriter(new FileStream(EventLogPath, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8)
        {
            AutoFlush = true,
        };
    }

    private static string Timestamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static double? EncodedTimeOf(IReadOnlyList<MoodyEvent>? decodedEvents) =>
        decodedEvents is { Count: > 0 } && decodedEvents[0] is TimeStampEvent ts ? ts.Seconds : null;

    /// <summary>Record that a binary packet was received (Binary Log).</summary>
    public void LogBinaryPacket(int connectionId, int messageNumber, int size, IReadOnlyList<MoodyEvent>? decodedEvents)
    {
        string encodedTimeStr = decodedEvents is null
            ? "n/a (decode failed)"
            : EncodedTimeOf(decodedEvents) is { } t
                ? $"{t.ToString("F6", CultureInfo.InvariantCulture)}s"
                : "n/a (continuation message)";

        string line = $"{Timestamp()} | conn={connectionId} msg=#{messageNumber} size={size}B encoded_time={encodedTimeStr}";

        lock (_binaryLock)
        {
            _binaryWriter.WriteLine(line);
        }
    }

    /// <summary>Record every decoded event for one packet (Event Log).</summary>
    public void LogDecodedEvents(int connectionId, int messageNumber, int size, IReadOnlyList<MoodyEvent> decodedEvents)
    {
        string encodedTimeStr = EncodedTimeOf(decodedEvents) is { } t
            ? $"{t.ToString("F6", CultureInfo.InvariantCulture)}s"
            : "n/a";

        var sb = new StringBuilder();
        sb.Append(Timestamp()).Append(" | ");
        sb.Append($"Connection #{connectionId} | Message #{messageNumber} | {size} bytes | ")
          .Append($"{decodedEvents.Count} event(s) | encoded_time={encodedTimeStr}")
          .Append('\n');

        for (int i = 0; i < decodedEvents.Count; i++)
        {
            sb.Append($"  [{i + 1}/{decodedEvents.Count}] {EventFormatting.Format(decodedEvents[i])}").Append('\n');
        }

        sb.Append(Separator);

        lock (_eventLock)
        {
            _eventWriter.WriteLine(sb.ToString());
        }
    }

    /// <summary>Record that a packet failed to decode (Event Log).</summary>
    public void LogDecodeError(int connectionId, int messageNumber, int size, Exception error)
    {
        string block = $"{Timestamp()} | Connection #{connectionId} | Message #{messageNumber} | {size} bytes | " +
                        $"DECODE FAILED: {error.Message}\n{Separator}";

        lock (_eventLock)
        {
            _eventWriter.WriteLine(block);
        }
    }

    public void Dispose()
    {
        _binaryWriter.Dispose();
        _eventWriter.Dispose();
    }
}
