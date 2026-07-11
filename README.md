# MoodyBlues Backend

C# (.NET) backend for the MoodyBlues Unity project. It's an ASP.NET Core host that:

- Runs a `/handshake` endpoint Unity calls on startup, deciding whether the client needs to
  (re-)upload its scene and telling it which WebSocket URL to stream to.
- Accepts scene uploads (`.glb`, see below) on `/scenes/{sceneId}`.
- Hosts the WebSocket server (`/stream`) that receives the binary transform-streaming protocol
  described in `Spec.md` (see the Unity project at `MoodyBlues_Unity/Spec.md`) and decodes it into
  structured events.

## Requirements

- [.NET 9 SDK](https://dotnet.microsoft.com/download) (for local `dotnet build`/`dotnet test`)
- Docker + Docker Compose (for running the deployed stack -- see below)

## Build & test

```bash
dotnet build
dotnet test
```

## Running the server (Docker, Linux)

The whole stack (Postgres + backend) is defined in `docker-compose.yml` and built from the
`Dockerfile` in this repo. `deploy.sh` is a single self-contained script for standing it up on a
fresh Linux box (or managing it on one you already deployed to). See "HTTPS/TLS" below for how it
plugs into a reverse proxy for TLS termination.

```bash
curl -O https://raw.githubusercontent.com/Kraaven/MoodyBlues_Backend/main/deploy.sh
chmod +x deploy.sh
./deploy.sh              # clones the repo (if needed), builds, and starts everything
```

If you've already cloned the repo, just run `./deploy.sh` from inside it (or anywhere -- it
looks for a checkout next to itself, or in `./MoodyBlues_Backend`, before cloning a fresh one).

```
./deploy.sh start     # (default) build (if needed) and start Postgres + backend
./deploy.sh stop      # stop the stack; data is kept, so 'start' again is fast
./deploy.sh restart   # stop, then start
./deploy.sh reset     # stop, git fetch/pull latest, rebuild, start -- the update command
./deploy.sh logs      # tail logs from both containers
./deploy.sh status    # show container status
```

`docker-compose.yml` creates the `moodyblues` Postgres role/database that the backend's default
`ServerConfig` connection string already expects, and Postgres data / uploaded scenes / logs all
live in Docker named volumes, so `reset`/`restart` never lose data.

Database migrations are applied automatically on startup (`db.Database.MigrateAsync()` in
`Program.cs`), so no separate migration step is needed.

Before starting the stack, create a `.env` file next to `docker-compose.yml` with:

```
MOODYBLUES_PUBLIC_HOST=your-domain.example.com
```

See "HTTPS/TLS" immediately below for what this needs to point at and why.

### HTTPS/TLS

Unity's Player Settings reject plain HTTP/WS `UnityWebRequest`/`ClientWebSocket` connections by
default (`Allow downloads over HTTP` = `Not Allowed`), so a production deployment needs to actually
terminate TLS, not just bind Kestrel to a port.

This backend does not run its own TLS/reverse-proxy container -- if the host already runs
[Nginx Proxy Manager](https://nginxproxymanager.com/) (or similar) fronting other services, that
should be the single place handling TLS for everything on the box, rather than each app fighting
over port 80/443. Instead, `docker-compose.yml` joins the `backend` service to an **external**
Docker network named `proxy` (created independently, e.g. by whatever set up NPM) under the alias
`moodyblues-backend`, so NPM can reach it directly without it ever being published to the host or
the public internet.

- `MOODYBLUES_PUBLIC_SCHEME=https` and `MOODYBLUES_PUBLIC_PORT=443` (set in `docker-compose.yml`)
  tell the backend to hand Unity back `wss://`/`https://` URLs from `POST /handshake` instead of
  `ws://`/`http://`, matching what NPM actually serves to the internet.
- If your setup doesn't have an existing reverse proxy on the host, adjust the `networks:` section
  in `docker-compose.yml` accordingly (e.g. publish `8765` directly and run your own TLS proxy in
  front of it instead).

One-time setup (adjust names/network if your reverse proxy setup differs):

1. `docker network create proxy` if that external network doesn't already exist (skip this if
   your reverse proxy already created it, as ours does).
2. Start the stack (`./deploy.sh start`/`reset`) so the `backend` container joins that network.
3. In the NPM web UI, **Add Proxy Host**:
   - Domain Names: `MOODYBLUES_PUBLIC_HOST` (e.g. `moodyblues.kraaven.net`)
   - Scheme: `http`, Forward Hostname/IP: `moodyblues-backend`, Forward Port: `8765`
   - **Websockets Support: enabled** (required -- `/stream` is a WebSocket endpoint)
   - SSL tab: request a new Let's Encrypt certificate, enable **Force SSL**. This works even
     though the domain is Cloudflare-proxied -- NPM already does exactly this for the other
     domains on this box.
4. Make sure the DNS record for `MOODYBLUES_PUBLIC_HOST` points at this server (proxied through
   Cloudflare is fine, same as the rest of `*.kraaven.net`).

On the Unity side, `Assets/Resources/BluesClientConfig.asset`'s `backendBaseUrl` must match --
`https://your-domain.example.com` (no `:443`, it's the default port).

For local development (`dotnet run`, no Docker/reverse proxy), none of this applies: the server
still defaults to plain `http://localhost:8765`, which the Unity Editor (unlike Player builds)
never blocks regardless of the `Allow downloads over HTTP` setting.

### Running locally without Docker

If you'd rather run the backend directly (e.g. for debugging in an IDE), point it at any
Postgres with a `moodyblues` role/database -- `docker compose up -d db` starts just the database
part of the stack above -- then:

```bash
dotnet run --project src/MoodyBlues.Backend
```

By default the server listens on `http://localhost:8765`, serving `POST /handshake`,
`POST /scenes/{sceneId}`, and the `/stream` WebSocket endpoint the Unity client
(`BluesStreamer.ConnectAsync`) connects to (using the URL returned by `/handshake` -- it's opaque
to Unity, not a hardcoded constant). Every decoded event is logged in detail to the console so you
can confirm the wire protocol round-trips correctly.

Configuration is via environment variables (all optional):

| Variable | Default | Meaning |
|---|---|---|
| `MOODYBLUES_HOST` | `localhost` | Interface to bind (`0.0.0.0` for all interfaces -- set for you inside Docker) |
| `MOODYBLUES_PORT` | `8765` | Port to listen on (handshake, scene upload, and WebSocket all share this one port) |
| `MOODYBLUES_PUBLIC_HOST` | `MOODYBLUES_HOST`, else `localhost` | Hostname handed back to clients in `webSocketUrl`/`sceneUploadUrl` -- must be a real domain reachable from the internet in production (see "HTTPS/TLS" above) |
| `MOODYBLUES_PUBLIC_SCHEME` | `http` | `http` or `https` -- controls whether clients are told to use `ws`/`http` or `wss`/`https`. Set to `https` only once a TLS reverse proxy is actually in front of the server |
| `MOODYBLUES_PUBLIC_PORT` | `MOODYBLUES_PORT` | Port handed back to clients; omitted from the URL entirely when it's the scheme's default (443 for https, 80 for http) |
| `MOODYBLUES_LOG_RAW_BYTES` | `1` | Log a hex preview of each raw message at debug level |
| `MOODYBLUES_LOG_DIR` | `logs` | Directory for the runtime log files below |
| `MOODYBLUES_DB_CONNECTION` | `Host=localhost;Port=5432;Database=moodyblues;Username=moodyblues;Password=moodyblues` | Postgres connection string (Developers/Scenes metadata) |
| `MOODYBLUES_SCENES_DIR` | `scenes` | Directory uploaded `.glb` files are written to (one subfolder per developer) |

### Runtime logs

While running, the server also maintains two persistent log files under `logs/`
(created automatically):

- `logs/binary.log` -- one line per WebSocket binary message received: when it
  arrived, its size in bytes, and the `Seconds` value from its `TimeStamp` event
  (the time the message represents on Unity's clock).
- `logs/events.log` -- every event decoded from every message, with a header per
  message and a long `----...----` separator between each message's block of
  events.

## HTTP API

### `POST /handshake`

Called once by Unity on startup, before it connects to the event WebSocket.

```json
{ "developerId": "string", "sceneId": "string", "sceneHash": "string", "sessionId": "string" }
```

->

```json
{ "webSocketUrl": "ws://host:port/stream?session=...", "sceneUploadRequired": true, "sceneUploadUrl": "http://host:port/scenes/{sceneId}?developerId=...&sceneHash=..." }
```

(`ws`/`http` above assume the plain local-dev setup; behind the Caddy TLS proxy described in
"HTTPS/TLS" above, these come back as `wss`/`https` with no port instead.)

`sceneUploadUrl` is present only when `sceneUploadRequired` is `true` -- i.e. no scene is on file
for `(developerId, sceneId)` yet, or its stored hash doesn't match `sceneHash`. `DeveloperId` rows
are auto-provisioned on first sight (no registration/auth yet -- see `Data/Developer.cs`).
`sceneHash` is stored and compared as an opaque string; the backend does not independently
recompute it from the uploaded scene file this milestone.

The WebSocket connection that follows must include the same `sessionId` as a `?session=` query
parameter -- it's a single-use token correlating the socket with the handshake that preceded it,
since the binary event wire protocol itself carries no developer/scene context.

### `POST /scenes/{sceneId}?developerId=...&sceneHash=...`

Raw `.glb` bytes in the request body (`Content-Type: model/gltf-binary`; gzip `Content-Encoding` is
honored). The file is written as-is to `scenes/{developerId}/{sceneId}.glb` and
`(developerId, sceneId)`'s stored hash is updated to `sceneHash`, so the next handshake sees it as
up to date. No GLTF parsing happens server-side this milestone (see `Scenes/SceneUploadEndpoints.cs`)
-- building a server-side object registry from the file's `node.extras.objectId` values is a later
milestone.

## Project layout

```
src/MoodyBlues.Backend/
  Program.cs                  Entry point: ASP.NET Core host, DI wiring, endpoint mapping
  Config/
    ServerConfig.cs            Host/port/log/DB/scenes settings, read from env vars
  Common/
    PathSegments.cs             Validates untrusted IDs before they touch the filesystem/URLs
  Data/
    MoodyBluesDbContext.cs      EF Core context (Postgres via Npgsql)
    Developer.cs, Scene.cs      Entities
    MoodyBluesDbContextFactory.cs  Design-time factory for `dotnet ef migrations`
  Migrations/                  EF Core migrations
  Handshake/
    HandshakeContracts.cs       Request/response DTOs
    HandshakeEndpoints.cs       POST /handshake
    PendingSessionStore.cs      In-memory handshake-to-WebSocket session correlation
  Scenes/
    SceneUploadEndpoints.cs     POST /scenes/{sceneId}
  Logging/
    ConsoleLog.cs                Simple timestamped, colorized console logging
    RuntimeLogs.cs                binary.log / events.log file writers
  Protocol/
    EventType.cs                  Wire event type IDs (Spec.md Section 5)
    ProtocolConstants.cs          Payload sizes and quantization ranges (Section 7)
    Quantize.cs                   Dequantization math
    Events.cs                     One record type per decoded event
    EventParser.cs                Decodes a raw message into a list of Events
    EventFormatting.cs             Human-readable rendering of a decoded event
    ProtocolException.cs           Thrown on malformed input
  Server/
    MoodyBluesServer.cs           Per-WebSocket-connection receive/decode loop
tests/MoodyBlues.Backend.Tests/
  EncodingHelpers.cs            Test-only encoders mirroring the spec's encode side
  ProtocolParserTests.cs        Round-trip / known-vector tests for the decoder
  HandshakeEndpointsTests.cs     Tests for POST /handshake (EF Core InMemory + fake HttpContext)
  SceneUploadEndpointsTests.cs   Tests for POST /scenes/{sceneId}
Dockerfile                      Multi-stage build for the backend image
docker-compose.yml               Postgres + backend stack definition (see "HTTPS/TLS" for how
                                  it plugs into an existing reverse proxy)
deploy.sh                        One-file clone/build/start/stop/reset script for a Linux host
```

## Protocol

See `MoodyBlues_Unity/Spec.md` for the authoritative wire format description. This backend
implements decoding only for now (no server-side object registry built from uploaded scenes yet)
-- the current milestone is: negotiate whether a scene upload is needed, accept and store it
as-is, and decode/log every event type on the WebSocket so the wire format can be validated
end-to-end against the real Unity client.
