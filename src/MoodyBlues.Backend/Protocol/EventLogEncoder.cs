using System.Buffers.Binary;
using System.Numerics;

namespace MoodyBlues.Backend.Protocol;

/// <summary>
/// Encodes the lifecycle / session subset of the Unity wire protocol (EventTypes 24–28) used for
/// the stripped per-session event log. Transform events (1–23) are intentionally not supported.
/// </summary>
public static class EventLogEncoder
{
    public const int TimeStampSize = ProtocolConstants.TimestampSize;
    public const int EnvelopeOnlySize = ProtocolConstants.EnvelopeSize;
    public const int InstantiateObjectSize = ProtocolConstants.EnvelopeSize + 20;

    /// <summary>Largest single event this encoder can produce (InstantiateObject).</summary>
    public const int MaxEventSize = InstantiateObjectSize;

    public static int Encode(MoodyEvent evt, Span<byte> destination)
    {
        return evt switch
        {
            TimeStampEvent ts => EncodeTimeStamp(ts.Seconds, destination),
            ShowObjectEvent show => EncodeEnvelope(EventType.ShowObject, show.ObjectId, destination),
            HideObjectEvent hide => EncodeEnvelope(EventType.HideObject, hide.ObjectId, destination),
            DeleteObjectEvent delete => EncodeEnvelope(EventType.DeleteObject, delete.ObjectId, destination),
            InstantiateObjectEvent inst => EncodeInstantiateObject(
                inst.ObjectId, inst.TemplateObjectId, inst.ParentObjectId,
                inst.Position, inst.Rotation, inst.Scale, destination),
            _ => throw new ArgumentException(
                $"EventLogEncoder only encodes lifecycle events (24–28); got {evt.GetType().Name}",
                nameof(evt)),
        };
    }

    public static int EncodeTimeStamp(double seconds, Span<byte> destination)
    {
        if (destination.Length < TimeStampSize)
        {
            throw new ArgumentException($"Destination needs at least {TimeStampSize} bytes.", nameof(destination));
        }

        destination[0] = (byte)EventType.TimeStamp;
        BinaryPrimitives.WriteDoubleLittleEndian(destination.Slice(1, 8), seconds);
        return TimeStampSize;
    }

    public static int EncodeShowObject(ushort objectId, Span<byte> destination) =>
        EncodeEnvelope(EventType.ShowObject, objectId, destination);

    public static int EncodeHideObject(ushort objectId, Span<byte> destination) =>
        EncodeEnvelope(EventType.HideObject, objectId, destination);

    public static int EncodeDeleteObject(ushort objectId, Span<byte> destination) =>
        EncodeEnvelope(EventType.DeleteObject, objectId, destination);

    public static int EncodeInstantiateObject(
        ushort newObjectId,
        ushort templateObjectId,
        ushort parentObjectId,
        Vector3 position,
        Quaternion rotation,
        Vector3 scale,
        Span<byte> destination)
    {
        if (destination.Length < InstantiateObjectSize)
        {
            throw new ArgumentException($"Destination needs at least {InstantiateObjectSize} bytes.", nameof(destination));
        }

        EncodeEnvelope(EventType.InstantiateObject, newObjectId, destination);
        int offset = ProtocolConstants.EnvelopeSize;
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(offset, 2), templateObjectId);
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(offset, 2), parentObjectId);
        offset += 2;
        Quantize.EncodeVector3_16(position, ProtocolConstants.RangePosition, destination.Slice(offset, 6));
        offset += 6;
        Quantize.EncodeQuatSmallestThree(rotation, ProtocolConstants.RangeQuatSmallestThree, destination.Slice(offset, 4));
        offset += 4;
        Quantize.EncodeVector3_16(scale, ProtocolConstants.RangeScaleTrue, destination.Slice(offset, 6));
        return InstantiateObjectSize;
    }

    private static int EncodeEnvelope(EventType eventType, ushort objectId, Span<byte> destination)
    {
        if (destination.Length < EnvelopeOnlySize)
        {
            throw new ArgumentException($"Destination needs at least {EnvelopeOnlySize} bytes.", nameof(destination));
        }

        destination[0] = (byte)eventType;
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(1, 2), objectId);
        return EnvelopeOnlySize;
    }
}
