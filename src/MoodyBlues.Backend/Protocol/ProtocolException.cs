namespace MoodyBlues.Backend.Protocol;

/// <summary>Thrown when a wire message cannot be decoded (unknown type, truncation, version mismatch).</summary>
public sealed class ProtocolException(string message) : Exception(message);
