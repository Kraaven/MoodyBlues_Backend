# MoodyBlues Backend

C# (.NET) backend for the MoodyBlues Unity project. It hosts a WebSocket server that
receives the binary transform-streaming protocol described in `Spec.md` (see the
Unity project at `MoodyBlues_Unity/Spec.md`) and decodes it into structured events.

## Requirements

- [.NET 9 SDK](https://dotnet.microsoft.com/download)

## Build & test

```powershell
dotnet build
dotnet test
```

## Running the server

```powershell
dotnet run --project src\MoodyBlues.Backend
```

By default the server listens on `ws://localhost:8765`, matching the URL the Unity
client (`BluesStreamer.ConnectAsync`) connects to. Every decoded event is logged in
detail to the console so you can confirm the wire protocol round-trips correctly.

Configuration is via environment variables (all optional):

| Variable | Default | Meaning |
|---|---|---|
| `MOODYBLUES_HOST` | `localhost` | Interface to bind (use `+` for all interfaces -- requires admin/URL ACL on Windows) |
| `MOODYBLUES_PORT` | `8765` | Port to listen on |
| `MOODYBLUES_LOG_RAW_BYTES` | `1` | Log a hex preview of each raw message at debug level |
| `MOODYBLUES_LOG_DIR` | `logs` | Directory for the runtime log files below |

### Runtime logs

While running, the server also maintains two persistent log files under `logs/`
(created automatically):

- `logs/binary.log` -- one line per WebSocket binary message received: when it
  arrived, its size in bytes, and the `Seconds` value from its `TimeStamp` event
  (the time the message represents on Unity's clock).
- `logs/events.log` -- every event decoded from every message, with a header per
  message and a long `----...----` separator between each message's block of
  events.

## Project layout

```
src/MoodyBlues.Backend/
  Program.cs                 Entry point: config, logging, server startup
  Config/
    ServerConfig.cs          Host/port/log settings, read from env vars
  Logging/
    ConsoleLog.cs             Simple timestamped, colorized console logging
    RuntimeLogs.cs             binary.log / events.log file writers
  Protocol/
    EventType.cs               Wire event type IDs (Spec.md Section 5)
    ProtocolConstants.cs        Payload sizes and quantization ranges (Section 7)
    Quantize.cs                 Dequantization math
    Events.cs                   One record type per decoded event
    EventParser.cs              Decodes a raw message into a list of Events
    EventFormatting.cs           Human-readable rendering of a decoded event
    ProtocolException.cs         Thrown on malformed input
  Server/
    MoodyBluesServer.cs         HttpListener-based WebSocket server + per-connection loop
tests/MoodyBlues.Backend.Tests/
  EncodingHelpers.cs           Test-only encoders mirroring the spec's encode side
  ProtocolParserTests.cs       Round-trip / known-vector tests for the decoder
```

## Protocol

See `MoodyBlues_Unity/Spec.md` for the authoritative wire format description. This
backend implements decoding only for now (no scene export / object store yet) --
the current milestone is: accept connections, decode every event type, and log
them (to console and to the two runtime log files) so the wire format can be
validated end-to-end against the real Unity client.
