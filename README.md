# MoodyBlues Backend

The server side of MoodyBlues, an ASP.NET Core (.NET 10) app backed by Postgres. It:

- receives recordings from Unity games: the handshake, the scene check, scene uploads, and the
  binary event stream (`/stream`, a WebSocket);
- stores each recording as an append-only raw log and compiles it into a replay file in the
  background;
- keeps every uploaded version of every scene, so old recordings keep replaying against the
  scene they were recorded in;
- serves the dashboard API (JWT-authenticated) used by `MoodyBlues_FrontEnd`.

The wire protocol is specified in `MoodyBlues_Unity/Spec.md`. If this README and the code disagree,
the code is right.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download), for `dotnet build` and `dotnet test`
- Docker with Docker Compose, for running the deployed stack

## Build and test

```bash
dotnet build
dotnet test
```

## Running the server (Docker, Linux)

`docker-compose.yml` defines the stack (Postgres and the backend), built from the `Dockerfile`.
`deploy.sh` stands it up on a Linux box and manages it afterwards.

```bash
curl -O https://raw.githubusercontent.com/Kraaven/MoodyBlues_Backend/main/deploy.sh
chmod +x deploy.sh
./deploy.sh              # clones the repo if needed, builds, and starts everything
```

```
./deploy.sh start     # (default) build if needed and start
./deploy.sh stop      # stop; data is kept
./deploy.sh restart   # stop, then start
./deploy.sh reset     # stop, git pull, rebuild, start: the update command
./deploy.sh logs      # follow both containers' logs
./deploy.sh status    # container status
```

All state lives in Docker named volumes (`moodyblues-db-data`, `moodyblues-scenes`,
`moodyblues-sessions`), so `reset` and `restart` never lose data. Database migrations run
automatically at startup.

Create a `.env` file next to `docker-compose.yml`:

```
MOODYBLUES_DB_PASSWORD=<random password>
MOODYBLUES_PUBLIC_HOST=your-domain.example.com
MOODYBLUES_JWT_SECRET=<long random secret, e.g. `openssl rand -base64 48`>
MOODYBLUES_CORS_ORIGIN=https://your-dashboard.example.com,http://localhost:5173
```

Postgres sets its password only when its volume is first created. If you change
`MOODYBLUES_DB_PASSWORD` later, the backend can no longer log in. Either change the password
inside Postgres too, or start over with `docker compose down -v`, which **deletes all data**.

The Dockerfile's Node.js / `gltf-transform` / `toktx` layer takes several minutes on a cold build.
Docker caches it, so later builds are fast unless that block changes.

### Container security

- The server runs as the non-root `app` user from the .NET base image.
- The entrypoint (`docker-entrypoint.sh`) starts as root only to hand files in `/app/data` that
  older images created as root over to `app`, then drops root before the server starts.
- `docker-compose.yml` limits the backend's memory, CPUs and process count. Override them with
  `MOODYBLUES_MEMORY_LIMIT` (default `4g`) and `MOODYBLUES_CPU_LIMIT` (default `2`) in `.env`.
- Docker's container logs rotate at 20 MB × 5 files per container.
- No ports are published. Postgres is reachable only on the compose network, and the backend
  only through the reverse proxy (below).

### HTTPS / TLS

Unity Player builds refuse plain HTTP and WS, so production needs TLS. The backend doesn't
terminate TLS itself. It joins an external Docker network named `proxy` as
`moodyblues-backend`, and an existing reverse proxy (Nginx Proxy Manager on our server) serves it.

1. `docker network create proxy`, if that network doesn't exist yet.
2. Start the stack so the backend joins the network.
3. In Nginx Proxy Manager, **Add Proxy Host**:
   - Domain: `MOODYBLUES_PUBLIC_HOST`
   - Scheme `http`, forward host `moodyblues-backend`, port `8765`
   - **Websockets Support: on** (`/stream` is a WebSocket)
   - SSL: a Let's Encrypt certificate, with **Force SSL** on
4. Point DNS at the server. Proxying through Cloudflare is fine.

`docker-compose.yml` sets `MOODYBLUES_PUBLIC_SCHEME=https` and `MOODYBLUES_PUBLIC_PORT=443`, so
the handshake hands clients `wss://` and `https://` URLs. Each scene config's `backendBaseUrl` in
Unity must be `https://your-domain.example.com`.

**Cloudflare and idle connections.** Cloudflare resets a WebSocket whose server side stays
silent for about a minute, even while the game is streaming to it. The server therefore sends a
one-byte `KeepAlive` message down every `/stream` connection every 10 seconds
(`MOODYBLUES_STREAM_HEARTBEAT_SECONDS`). The game ignores it.

**Rate limiting behind Cloudflare.** With `MOODYBLUES_TRUST_FORWARDED_HEADERS=true` (set in
compose), rate limits key on Cloudflare's `CF-Connecting-IP` header, falling back to
`X-Forwarded-For`. This is safe only while the backend can be reached solely through the proxy.

### Running locally without Docker

Start a Postgres with a `moodyblues` role and database (`docker compose up -d db` does that), then:

```bash
dotnet run --project src/MoodyBlues.Backend
```

It listens on `http://localhost:8765`. The Unity Editor allows plain HTTP, so a scene config can
point at it directly.

### Configuration

Everything is optional except where a deployment needs it (see `.env` above).

| Variable | Default | Meaning |
|---|---|---|
| `MOODYBLUES_HOST` | `localhost` | Bind address (`0.0.0.0` in Docker) |
| `MOODYBLUES_PORT` | `8765` | Listen port, for HTTP and the WebSocket |
| `MOODYBLUES_PUBLIC_HOST` | `MOODYBLUES_HOST` | Host handed to clients in `webSocketUrl` and `sceneUploadUrl` |
| `MOODYBLUES_PUBLIC_SCHEME` | `http` | `https` behind a TLS proxy, which makes the URLs `wss://` and `https://` |
| `MOODYBLUES_PUBLIC_PORT` | `MOODYBLUES_PORT` | Port handed to clients; left out of URLs when it is the scheme's default |
| `MOODYBLUES_DB_CONNECTION` | local `moodyblues` database | Postgres connection string |
| `MOODYBLUES_SCENES_DIR` | `data/scenes` | Scene files |
| `MOODYBLUES_SESSIONS_DIR` | `data/sessions` | Session raw logs and replay files |
| `MOODYBLUES_JWT_SECRET` | insecure dev value | Signs dashboard logins. **Set it in production**; the server warns at startup if it is the default |
| `MOODYBLUES_CORS_ORIGIN` | `http://localhost:5173` | Comma-separated dashboard origins |
| `MOODYBLUES_TRUST_FORWARDED_HEADERS` | off | Trust `X-Forwarded-*` and `CF-Connecting-IP` (proxy deployments only) |
| `MOODYBLUES_STREAM_INACTIVITY_TIMEOUT_SECONDS` | `30` | Close a `/stream` connection that delivers no valid message for this long |
| `MOODYBLUES_STREAM_HEARTBEAT_SECONDS` | `10` | Interval of the server's KeepAlive on `/stream` |
| `MOODYBLUES_RECONNECT_GRACE_SECONDS` | `60` | How long a dropped recording waits for its game to reconnect before it is compiled |
| `MOODYBLUES_COMPILE_TIMEOUT_MINUTES` | `10` | One compile is stopped (and the session marked Failed) after this long |
| `MOODYBLUES_GLTF_TRANSFORM_CMD` | `gltf-transform` | Scene optimizer executable |
| `MOODYBLUES_OPTIMIZER_MEMORY_MB` | `2048` | Heap limit for the optimizer (Node) process |

**Quotas** (0 turns one off):

| Variable | Default | Limit |
|---|---|---|
| `MOODYBLUES_MAX_SCENE_UPLOAD_MB` | `512` | One scene upload, measured after decompression |
| `MOODYBLUES_MAX_REVISIONS_PER_SCENE` | `200` | Stored versions per scene, current and archived |
| `MOODYBLUES_MAX_CONCURRENT_SESSIONS` | `50` | Recordings in progress per project |
| `MOODYBLUES_MAX_SESSION_MB` | `512` | Raw log size of one recording; the stream is closed when it is reached |
| `MOODYBLUES_MAX_SESSION_MINUTES` | `360` | Length of one recording, from its handshake |
| `MOODYBLUES_MAX_STORAGE_GB` | `20` | All scene and session files of one project |

---

## How a recording flows

1. **Handshake.** The game calls `POST /handshake`, which creates a session in **Recording** and
   returns a WebSocket URL with a secret token.
2. **Scene check and upload.**
   - `POST /scenes/check` (or the handshake) says whether this scene version (its hash) is stored.
   - If not, the Unity editor (at build time or from the Tools menu) or the game uploads it to
     `POST /scenes/{sceneId}`.
   - Each new hash becomes a new numbered **version** of the scene.
3. **Streaming.** The game streams to `/stream`. Every valid message is appended to the session's
   raw log, tagged with its connection number. The game may reconnect; the new connection
   appends to the same log.
4. **End of recording.** The session is queued for compiling when either:
   - the game sends `EndSession` and closes, or
   - it has been gone for the reconnect grace period.
5. **Compile.** The compile worker builds the replay file from the raw log and that version's
   object registry. A session whose scene version hasn't been uploaded goes to
   **WaitingForScene**. It stays there with no time limit, and compiles as soon as that version
   is uploaded.

Session statuses: `Recording`, `Queued`, `Compiling`, `Ready`, `Failed`, `WaitingForScene`.

**Who can send data.** The Unity-facing endpoints accept only developer IDs that belong to a
dashboard project; create a project to get one. The ID ships inside game builds, so treat it as
public. The quotas above limit what a leaked ID can cost.

## Unity-facing API

### `POST /handshake`

```json
{ "developerId": "...", "sceneId": "...", "sceneHash": "<64 hex>", "sessionId": "<client GUID>" }
```

```json
{ "webSocketUrl": "wss://host/stream?token=...", "sceneUploadRequired": true, "sceneUploadUrl": "https://host/scenes/{sceneId}?developerId=...&sceneHash=..." }
```

- **Unknown developer ID:** 404.
- **Too many recordings in progress for the project:** 429.
- **Retries:** a retried handshake with the same `sessionId` reuses the session and issues a new
  token; the old token stops working.

### `POST /scenes/check`

```json
{ "developerId": "...", "sceneId": "...", "sceneHash": "<64 hex>" }
```

```json
{ "sceneUploadRequired": true, "sceneUploadUrl": "https://host/scenes/..." }
```

Same validation and rate limit as the handshake. It doesn't create a session.

### `POST /scenes/{sceneId}?developerId=...&sceneHash=...`

- **Body:** the `.glb`, usually with `Content-Encoding: gzip`. An optional
  `X-MoodyBlues-Skipped-Meshes: <n>` header reports meshes a runtime export couldn't read; the
  dashboard shows it as a warning on that version.
- **Versions are immutable.** Uploading a hash that is already stored returns 200 and changes
  nothing.
- **A new hash becomes the scene's current version,** and the previous ones are archived. Its
  waiting sessions are queued.
- **Storage:** each upload writes its own file under `{ScenesDir}/{developerId}/{sceneId}/{hash}/`,
  so concurrent uploads of the same hash can't replace each other's file.
- **Errors:** 404 unknown developer, 400 not a GLB, 413 too large, 507 version or storage quota
  reached.

The upload is processed in the background (`Scenes/Processing/`):
1. The **object registry** (`registry.json`) is built from the GLB's `extras.objectId` nodes,
   with node-count, depth and cycle limits, because the GLB is untrusted input.
2. `gltf-transform optimize` produces a Draco/KTX2 `optimized.glb` for the viewer. If that fails,
   the raw upload is served instead.

### `GET /stream?token=...` (WebSocket)

- **Messages:** binary only, each a run of whole events (protocol v1–v3; see `Spec.md`). A
  message that doesn't decode is recorded as a data gap.
- **Refused connections:** a wrong protocol version, or a connection that doesn't start with
  `BeginSession`, is refused.
- **Message size:** messages up to 160 KB are accepted; the client's largest setting is 128 KB.

## Dashboard API

Every `/api` route needs `Authorization: Bearer <JWT>` and checks that the caller owns the
project. Another user's data returns 404, so its existence isn't revealed. Enums are sent as
names, for example `"Ready"`.

| Route | Purpose |
|---|---|
| `POST /auth/register`, `POST /auth/login` | Create an account / sign in; returns a JWT (7 days) |
| `GET /auth/me` | The signed-in user |
| `GET /api/projects`, `POST /api/projects` | List / create projects; creating one issues its Developer ID |
| `GET /api/projects/{id}` | A project and its scenes (each with its current version number) |
| `GET /api/scenes/{dev}/{scene}` | The scene: current version, archived versions (newest first, with session counts and sizes), and the number of sessions waiting for an un-uploaded version |
| `PATCH /api/scenes/{dev}/{scene}` | Rename (`{ "displayName": "..." }`) |
| `DELETE /api/scenes/{dev}/{scene}` | Delete the scene: closes its live recordings, then removes every version, session and file |
| `DELETE /api/scenes/{dev}/{scene}/versions/{n}` | Delete one archived version and its sessions (the current version can only go with the scene) |
| `DELETE /api/scenes/{dev}/{scene}/archive` | Delete every archived version and their sessions |
| `GET /api/scenes/{dev}/{scene}/file?hash=` | A version's GLB (optimized if available); without `hash`, the current version |
| `GET /api/scenes/{dev}/{scene}/sessions` | Sessions, newest first, each with its scene version (null while not uploaded) and error message. `?version=n` filters to one version; `?awaitingScene=true` to sessions waiting for their scene |
| `GET /api/sessions/{id}` | Session details and recording health counters |
| `GET /api/sessions/{id}/replay` | The compiled replay file (gzip-encoded) |
| `POST /api/sessions/{id}/recompile` | Rebuild the replay from the raw log |
| `DELETE /api/sessions/{id}` | Delete one session (not while recording or compiling) |

Delete endpoints return `{ "deletedVersions", "deletedSessions", "freedBytes" }`.

## Storage formats

- **Raw log** (`raw.mblog`): an 8-byte magic, then records.
  - `MBRAWv02`: `[u8 kind][u32 length][i64 receivedUnixMs][u32 connection][payload]`.
  - `MBRAWv01`, from older servers: the same without `connection`. It is still read, and a v01
    log that is appended to stays v01.
- **Replay file** (`replay.mbr.gz`): format 2, documented in `Compiler/ReplayFileWriter.cs`.
  - Object kinds are 0 scene, 1 spawned, and 2 spawned part: a child of a spawned object that the
    viewer animates inside the clone (protocol v3).
  - `SessionCompiler.Version` is stored on each session; recompile older sessions to update them.

## Project layout

```
src/MoodyBlues.Backend/
  Program.cs                 host setup, DI, middleware, endpoint mapping
  Config/ServerConfig.cs     every setting and quota, from MOODYBLUES_* environment variables
  Common/                    PathSegments (ID validation), RateLimits, IdGenerator, WorkSignal
  Data/                      EF Core entities and context, IngestAccess, StorageQuota, DeveloperProvisioning
  Migrations/                EF Core migrations (applied at startup)
  Auth/                      registration, login, JWT, password hashing
  Projects/                  /api/projects
  Ingest/                    /handshake, /scenes/check, /stream, open-connection tracking, stream tokens
  Scenes/                    scene upload, dashboard scene routes, versions and deletion, object registry
  Scenes/Processing/         background registry build and gltf-transform optimization
  Sessions/                  dashboard session routes
  Compiler/                  raw log -> replay: reconstruction, curve fitting, replay file writer, worker
  Protocol/                  wire decoder (v1-v3), event types, dequantization
  Storage/                   file locations, raw log reader/writer
  Logging/                   console logging, exception handling
tests/MoodyBlues.Backend.Tests/   xUnit tests (EF Core InMemory, endpoint handlers called directly)
Dockerfile, docker-entrypoint.sh, docker-compose.yml, deploy.sh
```
