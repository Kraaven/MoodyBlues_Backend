using System.Buffers.Binary;
using System.Numerics;

namespace MoodyBlues.Backend.Protocol;

/// <summary>
/// Decodes the Unity client's binary event stream (Spec 4-9). One instance per WebSocket
/// connection: it holds that connection's quantization ranges, which <c>BeginSession</c> declares.
///
/// A message is a run of whole events with no outer framing, so an unknown type byte or a
/// truncated event cannot be skipped (Spec 10.3). <see cref="DecodeMessage"/> therefore validates
/// the whole message before emitting any event: a message is either delivered completely or not
/// at all, so a sink never sees half a message.
/// </summary>
public sealed class WireDecoder
{
    private readonly bool _requireBeginSession;

    public WireDecoder(bool requireBeginSession = true)
    {
        _requireBeginSession = requireBeginSession;
        Ranges = WireRanges.Default;
    }

    /// <summary>Ranges declared by the most recent BeginSession (defaults until then).</summary>
    public WireRanges Ranges { get; private set; }

    public bool HasBegunSession { get; private set; }

    /// <summary>Protocol version declared by the most recent BeginSession (0 until then).</summary>
    public ushort ProtocolVersion { get; private set; }

    /// <summary>Validates and then decodes one WebSocket message into <paramref name="sink"/>.</summary>
    /// <exception cref="ProtocolException">The message is malformed; nothing was emitted.</exception>
    public void DecodeMessage(ReadOnlySpan<byte> message, IWireEventSink sink)
    {
        Validate(message);
        Decode(message, sink);
    }

    /// <summary>
    /// Checks framing (every type known, no truncation), that the connection starts with a
    /// BeginSession, and that its version is supported. Does not change decoder state.
    /// </summary>
    public void Validate(ReadOnlySpan<byte> message)
    {
        bool begun = HasBegunSession;
        ushort version = ProtocolVersion;
        int offset = 0;
        while (offset < message.Length)
        {
            byte type = message[offset];

            if (type == (byte)EventType.BeginSession)
            {
                // The layout after the version depends on the version, so read it first.
                if (offset + 3 > message.Length)
                {
                    throw new ProtocolException($"Truncated BeginSession at offset {offset}.");
                }

                version = BinaryPrimitives.ReadUInt16LittleEndian(message.Slice(offset + 1, 2));
                if (version < ProtocolConstants.MinProtocolVersion || version > ProtocolConstants.ProtocolVersion)
                {
                    throw new ProtocolException(
                        $"Unsupported protocol version {version}; this server implements " +
                        $"{ProtocolConstants.MinProtocolVersion}-{ProtocolConstants.ProtocolVersion}.");
                }
            }

            int size = EventSize(type, offset, version);
            if (offset + size > message.Length)
            {
                throw new ProtocolException(
                    $"Truncated {(EventType)type} at offset {offset}: need {size} bytes, have {message.Length - offset}.");
            }

            if (type == (byte)EventType.BeginSession)
            {
                (_, WireRanges ranges) = ReadSettings(message.Slice(offset, size), version);
                if (!ranges.IsValid)
                {
                    throw new ProtocolException("BeginSession declares invalid quantization ranges.");
                }

                begun = true;
            }
            else if (_requireBeginSession && !begun)
            {
                throw new ProtocolException($"Expected BeginSession as the first event of the connection, got {(EventType)type}.");
            }

            int axisOffset = SingleAxisPayloadOffset((EventType)type);
            if (axisOffset >= 0)
            {
                ushort raw = BinaryPrimitives.ReadUInt16LittleEndian(
                    message.Slice(offset + ProtocolConstants.EnvelopeSize + axisOffset, 2));
                if ((raw & 0b11) == 3)
                {
                    throw new ProtocolException($"Invalid single-axis rotation axis id 3 in {(EventType)type} at offset {offset}.");
                }
            }

            offset += size;
        }
    }

    /// <summary>Payload offset of the axis+angle field for single-axis events, or -1.</summary>
    private static int SingleAxisPayloadOffset(EventType type) => type switch
    {
        EventType.TrueRotationSingleAxis => 0,
        EventType.DeltaRotationSingleAxis => 0,
        EventType.TruePositionRotationSingleAxis => 6,
        EventType.DeltaPositionRotationSingleAxis => 4,
        _ => -1,
    };

    private static int EventSize(byte type, int offset, ushort version) => type switch
    {
        (byte)EventType.TimeStamp => ProtocolConstants.TimeStampSize,
        (byte)EventType.BeginSession => ProtocolConstants.BeginSessionSizeFor(version),
        (byte)EventType.KeepAlive => ProtocolConstants.KeepAliveSize,
        (byte)EventType.EndSession => ProtocolConstants.EndSessionSize,
        (byte)EventType.Log => ProtocolConstants.LogSizeFor(version),
        _ when ProtocolConstants.TryGetPayloadSize(type, out int payload) => ProtocolConstants.EnvelopeSize + payload,
        _ => throw new ProtocolException($"Unknown event type {type} at offset {offset}."),
    };

    /// <summary>
    /// Capture rate and the 7 tunable ranges from a whole BeginSession event: float32 up to v2,
    /// float16 from v3. The client applies the decoded float16 values to itself, so both sides
    /// use bit-identical ranges.
    /// </summary>
    private static (float CaptureHz, WireRanges Ranges) ReadSettings(ReadOnlySpan<byte> evt, ushort version)
    {
        ReadOnlySpan<byte> d = evt[3..];
        Span<float> v = stackalloc float[8];
        bool compact = version >= ProtocolConstants.CompactVersion;
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = compact
                ? (float)BinaryPrimitives.ReadHalfLittleEndian(d[(i * 2)..])
                : BinaryPrimitives.ReadSingleLittleEndian(d[(i * 4)..]);
        }

        return (v[0], new WireRanges(v[1], v[2], v[3], v[4], v[5], v[6], v[7]));
    }

    private void Decode(ReadOnlySpan<byte> message, IWireEventSink sink)
    {
        int offset = 0;
        while (offset < message.Length)
        {
            byte type = message[offset];
            ushort version = type == (byte)EventType.BeginSession
                ? BinaryPrimitives.ReadUInt16LittleEndian(message.Slice(offset + 1, 2))
                : ProtocolVersion;
            int size = EventSize(type, offset, version);
            ReadOnlySpan<byte> evt = message.Slice(offset, size);
            offset += size;

            switch ((EventType)type)
            {
                case EventType.TimeStamp:
                    sink.OnTimeStamp(BinaryPrimitives.ReadDoubleLittleEndian(evt[1..]));
                    continue;

                case EventType.BeginSession:
                {
                    (float captureHz, WireRanges ranges) = ReadSettings(evt, version);
                    var info = new BeginSessionInfo(version, captureHz, ranges);
                    Ranges = info.Ranges;
                    ProtocolVersion = version;
                    HasBegunSession = true;
                    sink.OnBeginSession(info);
                    continue;
                }

                case EventType.KeepAlive:
                    sink.OnKeepAlive();
                    continue;

                case EventType.EndSession:
                    sink.OnEndSession(new EndSessionInfo(
                        (SessionEndReason)evt[1],
                        BinaryPrimitives.ReadUInt32LittleEndian(evt[2..]),
                        BinaryPrimitives.ReadUInt32LittleEndian(evt[6..]),
                        BinaryPrimitives.ReadUInt32LittleEndian(evt[10..]),
                        BinaryPrimitives.ReadUInt32LittleEndian(evt[14..])));
                    continue;

                case EventType.Log:
                {
                    // Text is zero-padded; it ends at the first zero byte.
                    ReadOnlySpan<byte> text = evt.Slice(2, ProtocolConstants.LogTextBytesFor(ProtocolVersion));
                    int end = text.IndexOf((byte)0);
                    sink.OnLog(evt[1], end < 0 ? text : text[..end]);
                    continue;
                }
            }

            ushort objectId = BinaryPrimitives.ReadUInt16LittleEndian(evt[1..]);
            ReadOnlySpan<byte> p = evt[ProtocolConstants.EnvelopeSize..];
            DecodeEnvelopeEvent((EventType)type, objectId, p, sink);
        }
    }

    private void DecodeEnvelopeEvent(EventType type, ushort id, ReadOnlySpan<byte> p, IWireEventSink sink)
    {
        WireRanges r = Ranges;
        switch (type)
        {
            case EventType.ShowObject:
                sink.OnShow(id);
                return;
            case EventType.HideObject:
                sink.OnHide(id);
                return;
            case EventType.DeleteObject:
                sink.OnDelete(id);
                return;

            case EventType.InstantiateObject:
                sink.OnInstantiate(
                    id,
                    BinaryPrimitives.ReadUInt16LittleEndian(p),
                    BinaryPrimitives.ReadUInt16LittleEndian(p[2..]),
                    TruePos(p[4..]),
                    TrueRot(p[10..]),
                    TrueScale(p[14..]));
                return;

            case EventType.ReparentObject:
                sink.OnReparent(
                    id,
                    BinaryPrimitives.ReadUInt16LittleEndian(p),
                    TruePos(p[2..]),
                    TrueRot(p[8..]),
                    TrueScale(p[12..]));
                return;
        }

        TransformUpdate u = type switch
        {
            EventType.TruePosition => Abs(pos: TruePos(p)),
            EventType.TrueRotation => Abs(rot: TrueRot(p)),
            EventType.TrueRotationSingleAxis => Abs(rot: TrueAxisRot(p)),
            EventType.TrueScale => Abs(scale: TrueScale(p)),
            EventType.TrueUniformScale => Abs(scale: Uniform(Quantize.DecodeScalar16(p, r.ScaleMin, r.ScaleMax))),

            EventType.DeltaPosition => Delta(pos: DeltaPos(p)),
            EventType.DeltaRotation => Delta(rot: DeltaRot(p)),
            EventType.DeltaRotationSingleAxis => Delta(rot: DeltaAxisRot(p)),
            EventType.DeltaScale => Delta(scale: DeltaScale3(p)),
            EventType.DeltaUniformScale => Delta(scale: DeltaScale1(p[0])),

            EventType.TrueTransform => Abs(TruePos(p), TrueRot(p[6..]), TrueScale(p[10..])),
            EventType.TruePositionRotation => Abs(TruePos(p), TrueRot(p[6..])),
            EventType.TrueRotationScale => Abs(rot: TrueRot(p), scale: TrueScale(p[4..])),
            EventType.TruePositionScale => Abs(TruePos(p), scale: TrueScale(p[6..])),
            EventType.TruePositionRotationUniformScale => Abs(
                TruePos(p), TrueRot(p[6..]), Uniform(Quantize.DecodeScalar16(p[10..], r.ScaleMin, r.ScaleMax))),
            EventType.TruePositionRotationSingleAxis => Abs(TruePos(p), TrueAxisRot(p[6..])),

            EventType.DeltaTransform => Delta(DeltaPos(p), DeltaRot(p[4..]), DeltaScale3(p[7..])),
            EventType.DeltaPositionRotation => Delta(DeltaPos(p), DeltaRot(p[4..])),
            EventType.DeltaRotationScale => Delta(rot: DeltaRot(p), scale: DeltaScale3(p[3..])),
            EventType.DeltaPositionScale => Delta(DeltaPos(p), scale: DeltaScale3(p[4..])),
            EventType.DeltaPositionRotationSingleAxis => Delta(DeltaPos(p), DeltaAxisRot(p[4..])),
            EventType.DeltaPositionUniformScale => Delta(DeltaPos(p), scale: DeltaScale1(p[4])),
            EventType.DeltaRotationUniformScale => Delta(rot: DeltaRot(p), scale: DeltaScale1(p[3])),

            _ => throw new ProtocolException($"Unhandled event type {type}."),
        };

        sink.OnTransform(id, u);
    }

    private Vector3 TruePos(ReadOnlySpan<byte> p) => Quantize.DecodeVector3_16(p, -Ranges.PositionRange, Ranges.PositionRange);

    private Vector3 TrueScale(ReadOnlySpan<byte> p) => Quantize.DecodeVector3_16(p, Ranges.ScaleMin, Ranges.ScaleMax);

    private static Quaternion TrueRot(ReadOnlySpan<byte> p) => Quantize.DecodeQuatSmallestThree(p);

    private static Quaternion TrueAxisRot(ReadOnlySpan<byte> p) =>
        Quantize.DecodeAxisAngle(p, ProtocolConstants.TrueSingleAxisAngleMin, ProtocolConstants.TrueSingleAxisAngleMax);

    private Vector3 DeltaPos(ReadOnlySpan<byte> p) => Quantize.DecodeDeltaPosition(p, Ranges.DeltaPositionRange);

    private Quaternion DeltaRot(ReadOnlySpan<byte> p) => Quantize.DecodeQuatDropW(p, Ranges.DeltaRotationComponentRange);

    private Quaternion DeltaAxisRot(ReadOnlySpan<byte> p) =>
        Quantize.DecodeAxisAngle(p, -Ranges.DeltaSingleAxisAngleRange, Ranges.DeltaSingleAxisAngleRange);

    private Vector3 DeltaScale3(ReadOnlySpan<byte> p) => Quantize.DecodeVector3_8(p, -Ranges.DeltaScaleRange, Ranges.DeltaScaleRange);

    private Vector3 DeltaScale1(byte raw) => Uniform(Quantize.DecodeScalar8(raw, -Ranges.DeltaScaleRange, Ranges.DeltaScaleRange));

    private static Vector3 Uniform(float v) => new(v, v, v);

    private static TransformUpdate Abs(Vector3? pos = null, Quaternion? rot = null, Vector3? scale = null) =>
        Build(pos, rot, scale, delta: false);

    private static TransformUpdate Delta(Vector3? pos = null, Quaternion? rot = null, Vector3? scale = null) =>
        Build(pos, rot, scale, delta: true);

    private static TransformUpdate Build(Vector3? pos, Quaternion? rot, Vector3? scale, bool delta)
    {
        TransformFields f = TransformFields.None;
        if (pos.HasValue) f |= TransformFields.Position | (delta ? TransformFields.PositionIsDelta : 0);
        if (rot.HasValue) f |= TransformFields.Rotation | (delta ? TransformFields.RotationIsDelta : 0);
        if (scale.HasValue) f |= TransformFields.Scale | (delta ? TransformFields.ScaleIsDelta : 0);
        return new TransformUpdate(f, pos ?? default, rot ?? Quaternion.Identity, scale ?? default);
    }
}
