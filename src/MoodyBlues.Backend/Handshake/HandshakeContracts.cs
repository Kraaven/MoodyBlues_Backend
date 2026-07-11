namespace MoodyBlues.Backend.Handshake;

/// <summary>
/// Body of <c>POST /handshake</c>. Serialized as camelCase JSON (ASP.NET
/// Core minimal API default) -- field names here must match the contract
/// agreed with the Unity client exactly.
/// </summary>
public sealed record HandshakeRequest(string DeveloperId, string SceneId, string SceneHash, string SessionId);

/// <summary>Response body of <c>POST /handshake</c>.</summary>
public sealed record HandshakeResponse(string WebSocketUrl, bool SceneUploadRequired, string? SceneUploadUrl);
