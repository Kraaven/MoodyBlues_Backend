using System.Buffers.Binary;
using System.Numerics;
using MoodyBlues.Backend.Protocol;
using static MoodyBlues.Backend.Tests.EncodingHelpers;

namespace MoodyBlues.Backend.Tests;

/// <summary>
/// Builds wire messages the way the Unity client's Serializer does, for decoder and compiler
/// tests. It also mirrors the client's delta feedback loop: <see cref="DeltaPosition"/> returns the
/// value the decoder will reconstruct, so tests can track exactly what the client baselined.
/// </summary>
public sealed class WireBuilder
{
    private readonly List<byte> _bytes = [];

    public WireRanges Ranges { get; init; } = WireRanges.Default;

    public byte[] Build() => _bytes.ToArray();

    public WireBuilder BeginSession(float captureHz = 40f, ushort version = ProtocolConstants.ProtocolVersion)
    {
        var b = new byte[ProtocolConstants.BeginSessionSize];
        b[0] = (byte)EventType.BeginSession;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), version);
        WireRanges r = Ranges;
        float[] values = [captureHz, r.PositionRange, r.ScaleMin, r.ScaleMax, r.DeltaPositionRange, r.DeltaRotationComponentRange, r.DeltaSingleAxisAngleRange, r.DeltaScaleRange];
        for (int i = 0; i < values.Length; i++)
        {
            // Settings are float16, like the Unity client sends them.
            BinaryPrimitives.WriteHalfLittleEndian(b.AsSpan(3 + (i * 2)), (Half)values[i]);
        }

        _bytes.AddRange(b);
        return this;
    }

    public WireBuilder EndSession(SessionEndReason reason = SessionEndReason.ApplicationQuit, uint promotions = 0, uint widenings = 0, uint hardClamps = 0, uint dropped = 0)
    {
        var b = new byte[ProtocolConstants.EndSessionSize];
        b[0] = (byte)EventType.EndSession;
        b[1] = (byte)reason;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(2), promotions);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(6), widenings);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(10), hardClamps);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(14), dropped);
        _bytes.AddRange(b);
        return this;
    }

    public WireBuilder KeepAlive()
    {
        _bytes.Add((byte)EventType.KeepAlive);
        return this;
    }

    public WireBuilder Time(double seconds)
    {
        _bytes.AddRange(TimeStampBytes(seconds));
        return this;
    }

    public WireBuilder Raw(params byte[] bytes)
    {
        _bytes.AddRange(bytes);
        return this;
    }

    public WireBuilder Show(ushort id) => Raw(Envelope(EventType.ShowObject, id));

    public WireBuilder Hide(ushort id) => Raw(Envelope(EventType.HideObject, id));

    public WireBuilder Delete(ushort id) => Raw(Envelope(EventType.DeleteObject, id));

    public WireBuilder TruePosition(ushort id, Vector3 p) =>
        Raw(Concat(Envelope(EventType.TruePosition, id), PackVector3_16(p, (-Ranges.PositionRange, Ranges.PositionRange))));

    public WireBuilder TrueRotation(ushort id, Quaternion q) =>
        Raw(Concat(Envelope(EventType.TrueRotation, id), PackQuatSmallestThree(q, (-ProtocolConstants.TrueRotationComponentRange, ProtocolConstants.TrueRotationComponentRange))));

    public WireBuilder TrueTransform(ushort id, Vector3 p, Quaternion q, Vector3 s) => Raw(Concat(
        Envelope(EventType.TrueTransform, id),
        PackVector3_16(p, (-Ranges.PositionRange, Ranges.PositionRange)),
        PackQuatSmallestThree(q, (-ProtocolConstants.TrueRotationComponentRange, ProtocolConstants.TrueRotationComponentRange)),
        PackVector3_16(s, (Ranges.ScaleMin, Ranges.ScaleMax))));

    public WireBuilder DeltaPosition(ushort id, Vector3 delta) =>
        Raw(Concat(Envelope(EventType.DeltaPosition, id), PackDeltaPosition(delta, (-Ranges.DeltaPositionRange, Ranges.DeltaPositionRange))));

    public WireBuilder DeltaRotationSingleAxis(ushort id, int axis, float degrees) =>
        Raw(Concat(Envelope(EventType.DeltaRotationSingleAxis, id), PackAxisAngle(axis, degrees, (-Ranges.DeltaSingleAxisAngleRange, Ranges.DeltaSingleAxisAngleRange))));

    public WireBuilder Instantiate(ushort id, ushort template, ushort parent, Vector3 p, Quaternion q, Vector3 s) => Raw(Concat(
        Envelope(EventType.InstantiateObject, id),
        UShort(template),
        UShort(parent),
        PackVector3_16(p, (-Ranges.PositionRange, Ranges.PositionRange)),
        PackQuatSmallestThree(q, (-ProtocolConstants.TrueRotationComponentRange, ProtocolConstants.TrueRotationComponentRange)),
        PackVector3_16(s, (Ranges.ScaleMin, Ranges.ScaleMax))));

    public WireBuilder Reparent(ushort id, ushort parent, Vector3 p, Quaternion q, Vector3 s) => Raw(Concat(
        Envelope(EventType.ReparentObject, id),
        UShort(parent),
        PackVector3_16(p, (-Ranges.PositionRange, Ranges.PositionRange)),
        PackQuatSmallestThree(q, (-ProtocolConstants.TrueRotationComponentRange, ProtocolConstants.TrueRotationComponentRange)),
        PackVector3_16(s, (Ranges.ScaleMin, Ranges.ScaleMax))));

    private static byte[] UShort(ushort v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        return b;
    }

    /// <summary>What the decoder reconstructs from a quantized delta position (the client's feedback value).</summary>
    public Vector3 QuantizedDeltaPosition(Vector3 delta)
    {
        byte[] bytes = PackDeltaPosition(delta, (-Ranges.DeltaPositionRange, Ranges.DeltaPositionRange));
        return Quantize.DecodeDeltaPosition(bytes, Ranges.DeltaPositionRange);
    }
}
