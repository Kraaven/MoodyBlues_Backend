namespace MoodyBlues.Backend.Protocol;

/// <summary>Raised when a message can't be decoded per Spec.md.</summary>
public sealed class ProtocolException(string message) : Exception(message);
