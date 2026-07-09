using System.Buffers.Binary;

namespace MoodyBlues.Backend.Protocol;

/// <summary>
/// Decodes a raw WebSocket binary message into a list of Events (Spec.md Section 3).
///
/// Each message is a self-contained, back-to-back sequence of zero or more
/// events -- there's no outer length/header, so we just walk the buffer byte
/// by byte, dispatching on the leading EventType byte each time.
/// </summary>
public static class EventParser
{
    public static List<MoodyEvent> ParseMessage(ReadOnlySpan<byte> data)
    {
        var result = new List<MoodyEvent>();
        int offset = 0;
        int n = data.Length;

        while (offset < n)
        {
            byte rawType = data[offset];

            if (rawType == (byte)EventType.TimeStamp)
            {
                if (offset + ProtocolConstants.TimestampSize > n)
                {
                    throw new ProtocolException(
                        $"Truncated TimeStamp event at offset {offset}: need {ProtocolConstants.TimestampSize} bytes, have {n - offset}");
                }

                double seconds = BinaryPrimitives.ReadDoubleLittleEndian(data.Slice(offset + 1, 8));
                result.Add(new TimeStampEvent(seconds));
                offset += ProtocolConstants.TimestampSize;
                continue;
            }

            var eventType = (EventType)rawType;
            if (!ProtocolConstants.PayloadSizes.TryGetValue(eventType, out int payloadSize))
            {
                throw new ProtocolException($"Unknown EventType byte {rawType} at offset {offset}");
            }

            if (offset + ProtocolConstants.EnvelopeSize > n)
            {
                throw new ProtocolException($"Truncated envelope for {eventType} at offset {offset}");
            }

            ushort objectId = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 1, 2));
            int payloadOffset = offset + ProtocolConstants.EnvelopeSize;

            if (payloadOffset + payloadSize > n)
            {
                throw new ProtocolException(
                    $"Truncated payload for {eventType} (object {objectId}) at offset {offset}: " +
                    $"need {payloadSize} bytes, have {n - payloadOffset}");
            }

            result.Add(DecodePayload(eventType, objectId, data.Slice(payloadOffset, payloadSize)));
            offset = payloadOffset + payloadSize;
        }

        return result;
    }

    private static MoodyEvent DecodePayload(EventType eventType, ushort objectId, ReadOnlySpan<byte> payload)
    {
        var pos = ProtocolConstants.RangePosition;
        var scaleTrue = ProtocolConstants.RangeScaleTrue;
        var scaleDelta = ProtocolConstants.RangeScaleDelta;
        var quatTrue = ProtocolConstants.RangeQuatSmallestThree;
        var quatDelta = ProtocolConstants.RangeQuatDropW;
        var angleTrue = ProtocolConstants.RangeAngleTrueDeg;
        var angleDelta = ProtocolConstants.RangeAngleDeltaDeg;
        var deltaPos = ProtocolConstants.RangeDeltaPosition;

        switch (eventType)
        {
            case EventType.ShowObject:
                return new ShowObjectEvent(objectId);
            case EventType.HideObject:
                return new HideObjectEvent(objectId);
            case EventType.DeleteObject:
                return new DeleteObjectEvent(objectId);

            case EventType.InstantiateObject:
            {
                ushort templateId = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                var position = Quantize.DecodeVector3_16(payload.Slice(2, 6), pos);
                var rotation = Quantize.DecodeQuatSmallestThree(payload.Slice(8, 4), quatTrue);
                var scale = Quantize.DecodeVector3_16(payload.Slice(12, 6), scaleTrue);
                return new InstantiateObjectEvent(objectId, templateId, position, rotation, scale);
            }

            case EventType.TruePosition:
                return new TruePositionEvent(objectId, Quantize.DecodeVector3_16(payload, pos));

            case EventType.TrueRotation:
                return new TrueRotationEvent(objectId, Quantize.DecodeQuatSmallestThree(payload, quatTrue));

            case EventType.TrueRotationSingleAxis:
            {
                var (axis, angle) = Quantize.DecodeAxisAngle(payload, angleTrue);
                return new TrueRotationSingleAxisEvent(objectId, axis, angle);
            }

            case EventType.TrueScale:
                return new TrueScaleEvent(objectId, Quantize.DecodeVector3_16(payload, scaleTrue));

            case EventType.TrueUniformScale:
                return new TrueUniformScaleEvent(objectId, Quantize.DecodeScalar16(payload, scaleTrue));

            case EventType.DeltaPosition:
                return new DeltaPositionEvent(objectId, Quantize.DecodeDeltaPosition(payload, deltaPos));

            case EventType.DeltaRotation:
                return new DeltaRotationEvent(objectId, Quantize.DecodeQuatDropW(payload, quatDelta));

            case EventType.DeltaRotationSingleAxis:
            {
                var (axis, angle) = Quantize.DecodeAxisAngle(payload, angleDelta);
                return new DeltaRotationSingleAxisEvent(objectId, axis, angle);
            }

            case EventType.DeltaScale:
                return new DeltaScaleEvent(objectId, Quantize.DecodeVector3_8(payload, scaleDelta));

            case EventType.DeltaUniformScale:
                return new DeltaUniformScaleEvent(objectId, Quantize.DecodeScalar8(payload[0], scaleDelta));

            case EventType.TrueTransform:
            {
                var position = Quantize.DecodeVector3_16(payload, pos);
                var rotation = Quantize.DecodeQuatSmallestThree(payload.Slice(6, 4), quatTrue);
                var scale = Quantize.DecodeVector3_16(payload.Slice(10, 6), scaleTrue);
                return new TrueTransformEvent(objectId, position, rotation, scale);
            }

            case EventType.TruePositionRotation:
            {
                var position = Quantize.DecodeVector3_16(payload, pos);
                var rotation = Quantize.DecodeQuatSmallestThree(payload.Slice(6, 4), quatTrue);
                return new TruePositionRotationEvent(objectId, position, rotation);
            }

            case EventType.TrueRotationScale:
            {
                var rotation = Quantize.DecodeQuatSmallestThree(payload, quatTrue);
                var scale = Quantize.DecodeVector3_16(payload.Slice(4, 6), scaleTrue);
                return new TrueRotationScaleEvent(objectId, rotation, scale);
            }

            case EventType.TruePositionScale:
            {
                var position = Quantize.DecodeVector3_16(payload, pos);
                var scale = Quantize.DecodeVector3_16(payload.Slice(6, 6), scaleTrue);
                return new TruePositionScaleEvent(objectId, position, scale);
            }

            case EventType.TruePositionRotationUniformScale:
            {
                var position = Quantize.DecodeVector3_16(payload, pos);
                var rotation = Quantize.DecodeQuatSmallestThree(payload.Slice(6, 4), quatTrue);
                float uniformScale = Quantize.DecodeScalar16(payload.Slice(10, 2), scaleTrue);
                return new TruePositionRotationUniformScaleEvent(objectId, position, rotation, uniformScale);
            }

            case EventType.TruePositionRotationSingleAxis:
            {
                var position = Quantize.DecodeVector3_16(payload, pos);
                var (axis, angle) = Quantize.DecodeAxisAngle(payload.Slice(6, 2), angleTrue);
                return new TruePositionRotationSingleAxisEvent(objectId, position, axis, angle);
            }

            case EventType.DeltaTransform:
            {
                var position = Quantize.DecodeDeltaPosition(payload, deltaPos);
                var rotation = Quantize.DecodeQuatDropW(payload.Slice(4, 3), quatDelta);
                var scale = Quantize.DecodeVector3_8(payload.Slice(7, 3), scaleDelta);
                return new DeltaTransformEvent(objectId, position, rotation, scale);
            }

            case EventType.DeltaPositionRotation:
            {
                var position = Quantize.DecodeDeltaPosition(payload, deltaPos);
                var rotation = Quantize.DecodeQuatDropW(payload.Slice(4, 3), quatDelta);
                return new DeltaPositionRotationEvent(objectId, position, rotation);
            }

            case EventType.DeltaRotationScale:
            {
                var rotation = Quantize.DecodeQuatDropW(payload, quatDelta);
                var scale = Quantize.DecodeVector3_8(payload.Slice(3, 3), scaleDelta);
                return new DeltaRotationScaleEvent(objectId, rotation, scale);
            }

            case EventType.DeltaPositionScale:
            {
                var position = Quantize.DecodeDeltaPosition(payload, deltaPos);
                var scale = Quantize.DecodeVector3_8(payload.Slice(4, 3), scaleDelta);
                return new DeltaPositionScaleEvent(objectId, position, scale);
            }

            case EventType.DeltaPositionRotationSingleAxis:
            {
                var position = Quantize.DecodeDeltaPosition(payload, deltaPos);
                var (axis, angle) = Quantize.DecodeAxisAngle(payload.Slice(4, 2), angleDelta);
                return new DeltaPositionRotationSingleAxisEvent(objectId, position, axis, angle);
            }

            case EventType.DeltaPositionUniformScale:
            {
                var position = Quantize.DecodeDeltaPosition(payload, deltaPos);
                float uniformScale = Quantize.DecodeScalar8(payload[4], scaleDelta);
                return new DeltaPositionUniformScaleEvent(objectId, position, uniformScale);
            }

            case EventType.DeltaRotationUniformScale:
            {
                var rotation = Quantize.DecodeQuatDropW(payload, quatDelta);
                float uniformScale = Quantize.DecodeScalar8(payload[3], scaleDelta);
                return new DeltaRotationUniformScaleEvent(objectId, rotation, uniformScale);
            }

            default:
                throw new ProtocolException($"Unhandled event type: {eventType}");
        }
    }
}
