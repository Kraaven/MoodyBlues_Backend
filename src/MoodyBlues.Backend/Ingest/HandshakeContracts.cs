namespace MoodyBlues.Backend.Ingest;

/// <summary>
/// Body of <c>POST /handshake</c> (Unity Spec 13.1). camelCase JSON; field names must match the
/// Unity client's <c>HandshakeContracts.cs</c> exactly. The project comes from the ingest key.
/// </summary>
public sealed record HandshakeRequest(string SceneId, string SceneHash, string SessionId);

/// <summary>
/// Response of <c>POST /handshake</c>. <c>webSocketUrl</c> is opaque to the client, which opens it
/// with <c>streamToken</c> in the <c>X-MoodyBlues-Stream-Token</c> header.
/// </summary>
public sealed record HandshakeResponse(string WebSocketUrl, string StreamToken, bool SceneUploadRequired, string? SceneUploadUrl);

/// <summary>
/// Body of <c>POST /scenes/check</c> (Unity Spec 13). Field names must match the Unity client's
/// <c>SceneCheckRequest</c>.
/// </summary>
public sealed record SceneCheckRequest(string SceneId, string SceneHash);

/// <summary>Response of <c>POST /scenes/check</c>. <c>sceneUploadUrl</c> is set only when an upload is needed.</summary>
public sealed record SceneCheckResponse(bool SceneUploadRequired, string? SceneUploadUrl);

/// <summary>Response of <c>GET /connection</c>: the project the ingest key belongs to.</summary>
public sealed record ConnectionResponse(string ProjectName);
