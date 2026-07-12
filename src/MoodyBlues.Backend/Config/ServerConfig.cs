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

    /// <summary>
    /// Host/IP handed back to clients in <c>webSocketUrl</c>/<c>sceneUploadUrl</c> (see
    /// HandshakeEndpoints.cs). Deliberately separate from <see cref="Host"/>: that one is the
    /// Kestrel *bind* address and is typically "0.0.0.0" in Docker, which is not a value a
    /// client can ever connect back to. Defaults to <see cref="Host"/> for the plain
    /// `dotnet run` case where binding to localhost and reaching it as localhost are the same.
    /// </summary>
    public string PublicHost { get; init; } = "localhost";

    /// <summary>
    /// Scheme clients should use to reach <see cref="PublicHost"/> ("http" or "https"). Controls
    /// whether <c>webSocketUrl</c> comes back as <c>ws://</c>/<c>wss://</c> and
    /// <c>sceneUploadUrl</c> as <c>http://</c>/<c>https://</c> (see HandshakeEndpoints.cs). Set to
    /// "https" once a TLS-terminating reverse proxy (e.g. Caddy, see docker-compose.yml) is
    /// sitting in front of this server -- Kestrel itself never speaks TLS directly.
    /// </summary>
    public string PublicScheme { get; init; } = "http";

    /// <summary>
    /// Port handed back to clients in <c>webSocketUrl</c>/<c>sceneUploadUrl</c>. Defaults to
    /// <see cref="Port"/> (the plain `dotnet run`/no-proxy case). Behind a reverse proxy this is
    /// typically 443, which differs from the internal <see cref="Port"/> Kestrel actually binds.
    /// </summary>
    public int? PublicPort { get; init; }

    // Runtime log files (see Logging/RuntimeLogs.cs): binary.log tracks raw
    // packet reception, events.log tracks every decoded event.
    public string LogDir { get; init; } = "logs";
    public string BinaryLogFilename { get; init; } = "binary.log";
    public string EventLogFilename { get; init; } = "events.log";

    /// <summary>Postgres connection string for the Developers/Scenes metadata store.</summary>
    public string DbConnectionString { get; init; } = "Host=localhost;Port=5432;Database=moodyblues;Username=moodyblues;Password=moodyblues";

    /// <summary>Directory uploaded scene .glb files are written to (one subfolder per developer).</summary>
    public string ScenesDir { get; init; } = "scenes";

    /// <summary>
    /// Symmetric signing secret for the dashboard's JWTs (see <see cref="MoodyBlues.Backend.Auth.JwtTokenService"/>).
    /// The default is an insecure placeholder -- <c>Program.cs</c> logs a warning at startup if it's still in use,
    /// and production deployments must set <c>MOODYBLUES_JWT_SECRET</c> to a real random secret (at least 32 bytes).
    /// </summary>
    public string JwtSecret { get; init; } = "insecure-dev-only-jwt-secret-change-me-1234567890";

    /// <summary>
    /// Origins the dashboard SPA is served from, allowed via CORS. Comma-separated so both a local
    /// dev origin and a deployed one (e.g. a Vercel domain) can be allowed at the same time --
    /// see <c>MOODYBLUES_CORS_ORIGIN</c> below.
    /// </summary>
    public string[] CorsOrigins { get; init; } = ["http://localhost:5173"];

    public static ServerConfig FromEnvironment()
    {
        return new ServerConfig
        {
            Host = Environment.GetEnvironmentVariable("MOODYBLUES_HOST") ?? "localhost",
            Port = TryParseInt(Environment.GetEnvironmentVariable("MOODYBLUES_PORT"), 8765),
            PublicHost = Environment.GetEnvironmentVariable("MOODYBLUES_PUBLIC_HOST")
                ?? Environment.GetEnvironmentVariable("MOODYBLUES_HOST")
                ?? "localhost",
            PublicScheme = Environment.GetEnvironmentVariable("MOODYBLUES_PUBLIC_SCHEME") ?? "http",
            PublicPort = TryParseNullableInt(Environment.GetEnvironmentVariable("MOODYBLUES_PUBLIC_PORT")),
            LogRawBytes = !IsFalsey(Environment.GetEnvironmentVariable("MOODYBLUES_LOG_RAW_BYTES")),
            LogDir = Environment.GetEnvironmentVariable("MOODYBLUES_LOG_DIR") ?? "logs",
            BinaryLogFilename = Environment.GetEnvironmentVariable("MOODYBLUES_BINARY_LOG_FILENAME") ?? "binary.log",
            EventLogFilename = Environment.GetEnvironmentVariable("MOODYBLUES_EVENT_LOG_FILENAME") ?? "events.log",
            DbConnectionString = Environment.GetEnvironmentVariable("MOODYBLUES_DB_CONNECTION")
                ?? "Host=localhost;Port=5432;Database=moodyblues;Username=moodyblues;Password=moodyblues",
            ScenesDir = Environment.GetEnvironmentVariable("MOODYBLUES_SCENES_DIR") ?? "scenes",
            JwtSecret = Environment.GetEnvironmentVariable("MOODYBLUES_JWT_SECRET") ?? "insecure-dev-only-jwt-secret-change-me-1234567890",
            CorsOrigins = ParseCorsOrigins(Environment.GetEnvironmentVariable("MOODYBLUES_CORS_ORIGIN")),
        };
    }

    private static string[] ParseCorsOrigins(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? ["http://localhost:5173"]
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static int TryParseInt(string? value, int fallback) =>
        int.TryParse(value, out int parsed) ? parsed : fallback;

    private static int? TryParseNullableInt(string? value) =>
        int.TryParse(value, out int parsed) ? parsed : null;

    private static bool IsFalsey(string? value) =>
        value is "0" or "false" or "False";
}
