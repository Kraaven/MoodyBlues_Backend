using System.Numerics;

namespace MoodyBlues.Backend.Protocol;

/// <summary>Which properties a transform event carries, and whether each is absolute or a delta.</summary>
[Flags]
public enum TransformFields : byte
{
    None = 0,
    Position = 1 << 0,
    Rotation = 1 << 1,
    Scale = 1 << 2,
    PositionIsDelta = 1 << 3,
    RotationIsDelta = 1 << 4,
    ScaleIsDelta = 1 << 5,
}

/// <summary>
/// One decoded transform event (types 1-23), normalised into "which properties, absolute or
/// delta, and their values". Uniform scale arrives as a Vector3 with equal components and
/// single-axis rotations as a quaternion, so consumers never see the 23 wire variants.
/// </summary>
public readonly record struct TransformUpdate(
    TransformFields Fields,
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Scale)
{
    public bool Has(TransformFields field) => (Fields & field) != 0;
}

public readonly record struct BeginSessionInfo(ushort ProtocolVersion, float CaptureHz, WireRanges Ranges);

public readonly record struct EndSessionInfo(
    SessionEndReason Reason,
    uint DeltaPromotions,
    uint RotationWidenings,
    uint HardClamps,
    uint DroppedEvents);

/// <summary>
/// Receives decoded events from <see cref="WireDecoder"/>. A callback interface rather than a
/// list of event objects so decoding a long session allocates nothing per event.
/// </summary>
public interface IWireEventSink
{
    void OnTimeStamp(double seconds);

    void OnBeginSession(in BeginSessionInfo info);

    void OnKeepAlive();

    void OnEndSession(in EndSessionInfo info);

    void OnShow(ushort objectId);

    void OnHide(ushort objectId);

    void OnDelete(ushort objectId);

    void OnInstantiate(ushort objectId, ushort templateObjectId, ushort parentObjectId, Vector3 position, Quaternion rotation, Vector3 scale);

    void OnReparent(ushort objectId, ushort newParentObjectId, Vector3 position, Quaternion rotation, Vector3 scale);

    void OnTransform(ushort objectId, in TransformUpdate update);

    /// <summary>
    /// One Log event (Spec 6.7). <paramref name="style"/> holds the level in its top 2 bits (0
    /// means "continues the previous log") and the colour in its low 6 bits.
    /// <paramref name="utf8Text"/> is this event's share of the text, already cut at the padding.
    /// </summary>
    void OnLog(byte style, ReadOnlySpan<byte> utf8Text);
}

/// <summary>A sink that ignores everything; used when only validation is needed.</summary>
public sealed class NullWireEventSink : IWireEventSink
{
    public static readonly NullWireEventSink Instance = new();

    public void OnTimeStamp(double seconds) { }
    public void OnBeginSession(in BeginSessionInfo info) { }
    public void OnKeepAlive() { }
    public void OnEndSession(in EndSessionInfo info) { }
    public void OnShow(ushort objectId) { }
    public void OnHide(ushort objectId) { }
    public void OnDelete(ushort objectId) { }
    public void OnInstantiate(ushort objectId, ushort templateObjectId, ushort parentObjectId, Vector3 position, Quaternion rotation, Vector3 scale) { }
    public void OnReparent(ushort objectId, ushort newParentObjectId, Vector3 position, Quaternion rotation, Vector3 scale) { }
    public void OnTransform(ushort objectId, in TransformUpdate update) { }
    public void OnLog(byte style, ReadOnlySpan<byte> utf8Text) { }
}
