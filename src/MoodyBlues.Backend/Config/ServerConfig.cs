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

    /// <summary>
    /// Largest accepted WebSocket message. The client's largest message setting is 128 KB; the
    /// margin covers a final message that is slightly over its packet target.
    /// </summary>
    public int MaxStreamMessageBytes { get; init; } = 160 * 1024;

    /// <summary>Largest accepted scene upload, measured after gzip decompression.</summary>
    public long MaxSceneUploadBytes { get; init; } = 512L * 1024 * 1024;

    // ---- Quotas (security plan, phase 1). A value of 0 disables that quota. -------------------

    /// <summary>Stored scene versions per scene, current and archived together.</summary>
    public int MaxRevisionsPerScene { get; init; } = 200;

    /// <summary>Sessions per developer that are recording at the same time.</summary>
    public int MaxConcurrentSessionsPerDeveloper { get; init; } = 50;

    /// <summary>Raw log size of one session. The stream is closed once it is reached.</summary>
    public long MaxSessionRawLogBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Wall-clock length of one session, measured from its handshake.</summary>
    public TimeSpan MaxSessionDuration { get; init; } = TimeSpan.FromHours(6);

    /// <summary>All scene and session files of one developer (project) together.</summary>
    public long MaxStoragePerDeveloperBytes { get; init; } = 20L * 1024 * 1024 * 1024;

    /// <summary>One compile is cancelled after this long, so a pathological log cannot stall the worker.</summary>
    public TimeSpan CompileTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Heap limit for the gltf-transform (Node) process that optimizes uploaded scenes.</summary>
    public int OptimizerMemoryLimitMb { get; init; } = 2048;

    /// <summary>Executable for the scene optimization pass (the pinned <c>@gltf-transform/cli</c> in the Docker image).</summary>
    public string GltfTransformCommand { get; init; } = "gltf-transform";

    /// <summary>JWT signing secret. The default is insecure; Program.cs warns when it is in use.</summary>
    public string JwtSecret { get; init; } = InsecureDefaultJwtSecret;

    public const string InsecureDefaultJwtSecret = "insecure-dev-only-jwt-secret-change-me-1234567890";

    /// <summary>Origins the dashboard SPA is served from (comma-separated in the environment variable).</summary>
    public string[] CorsOrigins { get; init; } = ["http://localhost:5173"];

    /// <summary>Lifetime of the dashboard's access cookie (a JWT). Refreshed silently while the dashboard is open.</summary>
    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Lifetime of a refresh token. Each refresh replaces it, so this is how long a signed-in browser may stay idle.</summary>
    public TimeSpan RefreshTokenLifetime { get; init; } = TimeSpan.FromDays(14);

    /// <summary>
    /// SameSite mode of the dashboard cookies: "Lax", "Strict" or "None". Lax works when the
    /// dashboard and this API share a site (for example app.example.com and api.example.com).
    /// "None" is needed when they do not (for example a *.vercel.app dashboard), and requires
    /// HTTPS. Defaults to None over HTTPS and Lax over plain HTTP (local development).
    /// </summary>
    public string CookieSameSite { get; init; } = "Lax";

    /// <summary>Marks the dashboard cookies Secure (HTTPS only). On whenever <see cref="PublicScheme"/> is https.</summary>
    public bool CookieSecure => IsHttps;

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
            MaxSceneUploadBytes = TryParseInt(Env("MOODYBLUES_MAX_SCENE_UPLOAD_MB"), 512) * 1024L * 1024L,
            MaxRevisionsPerScene = TryParseInt(Env("MOODYBLUES_MAX_REVISIONS_PER_SCENE"), 200),
            MaxConcurrentSessionsPerDeveloper = TryParseInt(Env("MOODYBLUES_MAX_CONCURRENT_SESSIONS"), 50),
            MaxSessionRawLogBytes = TryParseInt(Env("MOODYBLUES_MAX_SESSION_MB"), 512) * 1024L * 1024L,
            MaxSessionDuration = TimeSpan.FromMinutes(TryParseInt(Env("MOODYBLUES_MAX_SESSION_MINUTES"), 360)),
            MaxStoragePerDeveloperBytes = TryParseInt(Env("MOODYBLUES_MAX_STORAGE_GB"), 20) * 1024L * 1024L * 1024L,
            CompileTimeout = TimeSpan.FromMinutes(Math.Max(1, TryParseInt(Env("MOODYBLUES_COMPILE_TIMEOUT_MINUTES"), 10))),
            OptimizerMemoryLimitMb = Math.Max(256, TryParseInt(Env("MOODYBLUES_OPTIMIZER_MEMORY_MB"), 2048)),
            GltfTransformCommand = Env("MOODYBLUES_GLTF_TRANSFORM_CMD") ?? "gltf-transform",
            JwtSecret = Env("MOODYBLUES_JWT_SECRET") ?? InsecureDefaultJwtSecret,
            CorsOrigins = ParseCorsOrigins(Env("MOODYBLUES_CORS_ORIGIN")),
            AccessTokenLifetime = TimeSpan.FromMinutes(Math.Clamp(TryParseInt(Env("MOODYBLUES_ACCESS_TOKEN_MINUTES"), 15), 1, 24 * 60)),
            RefreshTokenLifetime = TimeSpan.FromDays(Math.Clamp(TryParseInt(Env("MOODYBLUES_REFRESH_TOKEN_DAYS"), 14), 1, 365)),
            CookieSameSite = Env("MOODYBLUES_COOKIE_SAMESITE")
                ?? (string.Equals(Env("MOODYBLUES_PUBLIC_SCHEME"), "https", StringComparison.OrdinalIgnoreCase) ? "None" : "Lax"),
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
