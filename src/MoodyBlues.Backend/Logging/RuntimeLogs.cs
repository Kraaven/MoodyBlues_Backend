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

    private readonly FileLogChannel _binaryLog;
    private readonly FileLogChannel _eventLog;

    public string BinaryLogPath => _binaryLog.Path;
    public string EventLogPath => _eventLog.Path;

    public RuntimeLogs(string logDir, string binaryFilename, string eventFilename)
    {
        Directory.CreateDirectory(logDir);
        _binaryLog = new FileLogChannel(Path.Combine(logDir, binaryFilename));
        _eventLog = new FileLogChannel(Path.Combine(logDir, eventFilename));
    }

    private static string Timestamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static double? EncodedTimeOf(IReadOnlyList<MoodyEvent> decodedEvents) =>
        decodedEvents is { Count: > 0 } && decodedEvents[0] is TimeStampEvent ts ? ts.Seconds : null;

    /// <summary>Record a successfully decoded message: one binary-log line plus its full event-log block.</summary>
    public void LogMessage(int connectionId, int messageNumber, int size, IReadOnlyList<MoodyEvent> decodedEvents)
    {
        string encodedTimeStr = EncodedTimeOf(decodedEvents) is { } t
            ? $"{t.ToString("F6", CultureInfo.InvariantCulture)}s"
            : "n/a (continuation message)";

        _binaryLog.WriteLine($"{Timestamp()} | conn={connectionId} msg=#{messageNumber} size={size}B encoded_time={encodedTimeStr}");

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
        _eventLog.WriteLine(sb.ToString());
    }

    /// <summary>Record a message that failed to decode: one binary-log line plus an error block in the event log.</summary>
    public void LogDecodeFailure(int connectionId, int messageNumber, int size, Exception error)
    {
        _binaryLog.WriteLine($"{Timestamp()} | conn={connectionId} msg=#{messageNumber} size={size}B encoded_time=n/a (decode failed)");
        _eventLog.WriteLine(
            $"{Timestamp()} | Connection #{connectionId} | Message #{messageNumber} | {size} bytes | " +
            $"DECODE FAILED: {error.Message}\n{Separator}");
    }

    public void Dispose()
    {
        _binaryLog.Dispose();
        _eventLog.Dispose();
    }

    /// <summary>A single append-only, thread-safe log file (path + writer + lock bundled together).</summary>
    private sealed class FileLogChannel : IDisposable
    {
        private readonly object _lock = new();
        private readonly StreamWriter _writer;

        public string Path { get; }

        public FileLogChannel(string path)
        {
            Path = path;
            _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8)
            {
                AutoFlush = true,
            };
        }

        public void WriteLine(string line)
        {
            lock (_lock)
            {
                _writer.WriteLine(line);
            }
        }

        public void Dispose() => _writer.Dispose();
    }
}
