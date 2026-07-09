namespace MoodyBlues.Backend.Config;

/// <summary>
/// Runtime configuration for the WebSocket server. Defaults match Spec.md
/// Section 8 (<c>ws://localhost:8765</c>).
/// </summary>
public sealed class ServerConfig
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 8765;
    public bool LogRawBytes { get; init; } = true;

    // Runtime log files (see Logging/RuntimeLogs.cs): binary.log tracks raw
    // packet reception, events.log tracks every decoded event.
    public string LogDir { get; init; } = "logs";
    public string BinaryLogFilename { get; init; } = "binary.log";
    public string EventLogFilename { get; init; } = "events.log";

    public static ServerConfig FromEnvironment()
    {
        return new ServerConfig
        {
            Host = Environment.GetEnvironmentVariable("MOODYBLUES_HOST") ?? "localhost",
            Port = TryParseInt(Environment.GetEnvironmentVariable("MOODYBLUES_PORT"), 8765),
            LogRawBytes = !IsFalsey(Environment.GetEnvironmentVariable("MOODYBLUES_LOG_RAW_BYTES")),
            LogDir = Environment.GetEnvironmentVariable("MOODYBLUES_LOG_DIR") ?? "logs",
            BinaryLogFilename = Environment.GetEnvironmentVariable("MOODYBLUES_BINARY_LOG_FILENAME") ?? "binary.log",
            EventLogFilename = Environment.GetEnvironmentVariable("MOODYBLUES_EVENT_LOG_FILENAME") ?? "events.log",
        };
    }

    private static int TryParseInt(string? value, int fallback) =>
        int.TryParse(value, out int parsed) ? parsed : fallback;

    private static bool IsFalsey(string? value) =>
        value is "0" or "false" or "False";
}
