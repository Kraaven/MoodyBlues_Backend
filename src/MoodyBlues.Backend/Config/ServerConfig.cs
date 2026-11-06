namespace MoodyBlues.Backend.Config;

/// <summary>
/// Runtime configuration, read from <c>MOODYBLUES_*</c> environment variables (see
/// <see cref="FromEnvironment"/> and README.md for the full list).
/// </summary>
public sealed class ServerConfig
{
    /// <summary>Kestrel bind address. "0.0.0.0" in Docker.</summary>
    public string Host { get; init; } = "localhost";

    public int Port { get; init; } = 8765;

    /// <summary>
    /// Host clients can reach this server on, handed back in <c>webSocketUrl</c> and
    /// <c>sceneUploadUrl</c>. Separate from <see cref="Host"/> because a bind address such as
    /// 0.0.0.0 is not something a client can connect to.
    /// </summary>
    public string PublicHost { get; init; } = "localhost";

    /// <summary>"http" or "https"; controls ws:// vs wss:// in handshake URLs. TLS is terminated by the proxy.</summary>
    public string PublicScheme { get; init; } = "http";

    /// <summary>Port clients use; defaults to <see cref="Port"/>. Usually 443 behind a proxy.</summary>
    public int? PublicPort { get; init; }

    public string DbConnectionString { get; init; } = "Host=localhost;Port=5432;Database=moodyblues;Username=moodyblues;Password=moodyblues";

    /// <summary>Root for scene files: <c>{developerId}/{sceneId}/{hash}/raw.glb</c> and friends.</summary>
    public string ScenesDir { get; init; } = "data/scenes";

    /// <summary>Root for session files: <c>{developerId}/{sessionId}/raw.mblog</c> and <c>replay.mbr.gz</c>.</summary>
    public string SessionsDir { get; init; } = "data/sessions";

    /// <summary>
    /// A <c>/stream</c> connection that delivers no message for this long is closed. The client
    /// sends a KeepAlive every 5 s of real time, so this only fires when the client is gone.
    /// </summary>
    public TimeSpan StreamInactivityTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often the server sends a one-byte KeepAlive message down each <c>/stream</c>
    /// connection. The stream is otherwise client-to-server only, and a proxy in front of the
    /// backend (Cloudflare) cuts connections whose origin side stays silent, at about 60 s.
    /// </summary>
    public TimeSpan StreamHeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// After a connection drops without EndSession, how long to wait for the client to reconnect
    /// before sealing the session and compiling it. The client's reconnect backoff totals 15 s.
    /// </summary>
    public TimeSpan ReconnectGracePeriod { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a queued session waits for its scene revision to be uploaded before failing.</summary>
    public TimeSpan SceneUploadWaitLimit { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Largest accepted WebSocket message. The client sends at most 8 KB.</summary>
    public int MaxStreamMessageBytes { get; init; } = 64 * 1024;

    /// <summary>Largest accepted scene upload, measured after gzip decompression.</summary>
    public long MaxSceneUploadBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Executable for the scene optimization pass (the pinned <c>@gltf-transform/cli</c> in the Docker image).</summary>
    public string GltfTransformCommand { get; init; } = "gltf-transform";

    /// <summary>JWT signing secret. The default is insecure; Program.cs warns when it is in use.</summary>
    public string JwtSecret { get; init; } = InsecureDefaultJwtSecret;

    public const string InsecureDefaultJwtSecret = "insecure-dev-only-jwt-secret-change-me-1234567890";

    /// <summary>Origins the dashboard SPA is served from (comma-separated in the environment variable).</summary>
    public string[] CorsOrigins { get; init; } = ["http://localhost:5173"];

    public static ServerConfig FromEnvironment()
    {
        string? Env(string name) => Environment.GetEnvironmentVariable(name);

        return new ServerConfig
        {
            Host = Env("MOODYBLUES_HOST") ?? "localhost",
            Port = TryParseInt(Env("MOODYBLUES_PORT"), 8765),
            PublicHost = Env("MOODYBLUES_PUBLIC_HOST") ?? Env("MOODYBLUES_HOST") ?? "localhost",
            PublicScheme = Env("MOODYBLUES_PUBLIC_SCHEME") ?? "http",
            PublicPort = TryParseNullableInt(Env("MOODYBLUES_PUBLIC_PORT")),
            DbConnectionString = Env("MOODYBLUES_DB_CONNECTION")
                ?? "Host=localhost;Port=5432;Database=moodyblues;Username=moodyblues;Password=moodyblues",
            ScenesDir = Env("MOODYBLUES_SCENES_DIR") ?? "data/scenes",
            SessionsDir = Env("MOODYBLUES_SESSIONS_DIR") ?? "data/sessions",
            StreamInactivityTimeout = TimeSpan.FromSeconds(TryParseInt(Env("MOODYBLUES_STREAM_INACTIVITY_TIMEOUT_SECONDS"), 30)),
            StreamHeartbeatInterval = TimeSpan.FromSeconds(Math.Max(1, TryParseInt(Env("MOODYBLUES_STREAM_HEARTBEAT_SECONDS"), 10))),
            ReconnectGracePeriod = TimeSpan.FromSeconds(TryParseInt(Env("MOODYBLUES_RECONNECT_GRACE_SECONDS"), 60)),
            SceneUploadWaitLimit = TimeSpan.FromHours(TryParseInt(Env("MOODYBLUES_SCENE_UPLOAD_WAIT_HOURS"), 24)),
            MaxSceneUploadBytes = TryParseInt(Env("MOODYBLUES_MAX_SCENE_UPLOAD_MB"), 512) * 1024L * 1024L,
            GltfTransformCommand = Env("MOODYBLUES_GLTF_TRANSFORM_CMD") ?? "gltf-transform",
            JwtSecret = Env("MOODYBLUES_JWT_SECRET") ?? InsecureDefaultJwtSecret,
            CorsOrigins = ParseCorsOrigins(Env("MOODYBLUES_CORS_ORIGIN")),
        };
    }

    /// <summary>Public host plus port, omitting the port when it is the scheme's default.</summary>
    public string PublicHostAndPort()
    {
        int port = PublicPort ?? Port;
        bool isDefaultPort = IsHttps ? port == 443 : port == 80;
        return isDefaultPort ? PublicHost : $"{PublicHost}:{port}";
    }

    public bool IsHttps => string.Equals(PublicScheme, "https", StringComparison.OrdinalIgnoreCase);

    private static string[] ParseCorsOrigins(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? ["http://localhost:5173"]
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static int TryParseInt(string? value, int fallback) =>
        int.TryParse(value, out int parsed) ? parsed : fallback;

    private static int? TryParseNullableInt(string? value) =>
        int.TryParse(value, out int parsed) ? parsed : null;
}
