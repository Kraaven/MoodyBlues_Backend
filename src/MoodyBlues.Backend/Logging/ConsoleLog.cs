using System.Globalization;

namespace MoodyBlues.Backend.Logging;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>
/// Small, dependency-free, thread-safe console logger with per-level
/// timestamps and colors. Kept intentionally simple -- this project doesn't
/// need a full logging framework.
/// </summary>
public static class ConsoleLog
{
    private static readonly object SyncRoot = new();

    public static LogLevel MinLevel { get; set; } = LogLevel.Debug;

    public static void Debug(string message) => Write(LogLevel.Debug, message);

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warn(string message) => Write(LogLevel.Warn, message);

    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Error(string message, Exception exception) =>
        Write(LogLevel.Error, $"{message}{Environment.NewLine}{exception}");

    private static void Write(LogLevel level, string message)
    {
        if (level < MinLevel)
        {
            return;
        }

        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        (string label, ConsoleColor color) = level switch
        {
            LogLevel.Debug => ("DEBUG", ConsoleColor.DarkGray),
            LogLevel.Info => ("INFO ", ConsoleColor.Cyan),
            LogLevel.Warn => ("WARN ", ConsoleColor.Yellow),
            LogLevel.Error => ("ERROR", ConsoleColor.Red),
            _ => ("?????", ConsoleColor.Gray),
        };

        lock (SyncRoot)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{timestamp}] ");
            Console.ForegroundColor = color;
            Console.Write(label);
            Console.ForegroundColor = previous;
            Console.WriteLine($"  {message}");
        }
    }
}
