namespace MoodyBlues.Backend.Ingest;

/// <summary>
/// Body of <c>POST /handshake</c> (Unity Spec 13.1). camelCase JSON; field names must match the
/// Unity client's <c>HandshakeContracts.cs</c> exactly.
/// </summary>
public sealed record HandshakeRequest(string DeveloperId, string SceneId, string SceneHash, string SessionId);

/// <summary>Response of <c>POST /handshake</c>. <c>webSocketUrl</c> is opaque to the client.</summary>
public sealed record HandshakeResponse(string WebSocketUrl, bool SceneUploadRequired, string? SceneUploadUrl);
