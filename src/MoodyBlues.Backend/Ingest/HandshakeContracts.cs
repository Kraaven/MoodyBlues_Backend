namespace MoodyBlues.Backend.Ingest;

/// <summary>
/// Body of <c>POST /handshake</c> (Unity Spec 13.1). camelCase JSON; field names must match the
/// Unity client's <c>HandshakeContracts.cs</c> exactly.
/// </summary>
public sealed record HandshakeRequest(string DeveloperId, string SceneId, string SceneHash, string SessionId);

/// <summary>Response of <c>POST /handshake</c>. <c>webSocketUrl</c> is opaque to the client.</summary>
public sealed record HandshakeResponse(string WebSocketUrl, bool SceneUploadRequired, string? SceneUploadUrl);

/// <summary>
/// Body of <c>POST /scenes/check</c> (Unity Spec 13). Field names must match the Unity client's
/// <c>SceneCheckRequest</c>.
/// </summary>
public sealed record SceneCheckRequest(string DeveloperId, string SceneId, string SceneHash);

/// <summary>Response of <c>POST /scenes/check</c>. <c>sceneUploadUrl</c> is set only when an upload is needed.</summary>
public sealed record SceneCheckResponse(bool SceneUploadRequired, string? SceneUploadUrl);
