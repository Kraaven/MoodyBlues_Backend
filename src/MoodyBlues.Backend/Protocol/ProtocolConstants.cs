namespace MoodyBlues.Backend.Protocol;

/// <summary>
/// Fixed sizes and constants from the Unity repo's Spec.md (Sections 4, 6 and 12). Every value
/// here is traceable to that document; if they ever disagree, the Unity code is the source of
/// truth and this file is stale.
/// </summary>
public static class ProtocolConstants
{
    /// <summary>Newest protocol version this decoder implements (Spec 6.1, BeginSession).</summary>
    public const ushort ProtocolVersion = 3;

    /// <summary>
    /// Oldest version still accepted. Version 2 added the Log event. Version 3 shrank BeginSession
    /// (settings as 16-bit floats) and Log (30 text bytes) so no event exceeds 32 bytes, and made
    /// InstantiateObject cover the spawned object's whole subtree.
    /// </summary>
    public const ushort MinProtocolVersion = 1;

    /// <summary>First version with 16-bit BeginSession settings, 32-byte Log events and subtree spawns.</summary>
    public const ushort CompactVersion = 3;

    /// <summary>EventType + ObjectID, carried by every event except the session maintenance ones.</summary>
    public const int EnvelopeSize = 3;

    public const int TimeStampSize = 9;
    public const int KeepAliveSize = 1;
    public const int EndSessionSize = 18;

    /// <summary>type + version + captureHz + 7 ranges, as float32 (v1-v2).</summary>
    public const int BeginSessionSizeV2 = 1 + 2 + (8 * 4);

    /// <summary>type + version + captureHz + 7 ranges, as float16 (v3).</summary>
    public const int BeginSessionSizeV3 = 1 + 2 + (8 * 2);

    /// <summary>Kept for callers that only deal with the original layout.</summary>
    public const int BeginSessionSize = BeginSessionSizeV2;

    /// <summary>UTF-8 text bytes carried by one Log event (zero-padded), v2.</summary>
    public const int LogTextBytesV2 = 33;

    /// <summary>UTF-8 text bytes carried by one Log event (zero-padded), v3.</summary>
    public const int LogTextBytesV3 = 30;

    public static int BeginSessionSizeFor(ushort version) => version >= CompactVersion ? BeginSessionSizeV3 : BeginSessionSizeV2;

    public static int LogTextBytesFor(ushort version) => version >= CompactVersion ? LogTextBytesV3 : LogTextBytesV2;

    /// <summary>type + style + text.</summary>
    public static int LogSizeFor(ushort version) => 2 + LogTextBytesFor(version);

    /// <summary>Largest single event across every supported version (BeginSession and Log, v1-v2).</summary>
    public const int MaxEventSize = BeginSessionSizeV2;

    /// <summary>Absolute rotations use smallest-three over +/- 1/sqrt(2). Not tunable (Spec 9.5).</summary>
    public const float TrueRotationComponentRange = 0.70711f;

    /// <summary>Absolute single-axis angles cover a full turn (Spec 9.6). Not tunable.</summary>
    public const float TrueSingleAxisAngleMin = 0f;
    public const float TrueSingleAxisAngleMax = 360f;

    /// <summary>
    /// Payload size in bytes, excluding the 3-byte envelope, for every envelope-carrying event.
    /// Indexed by event type byte; -1 means "not an envelope event" or "unknown".
    /// </summary>
    private static readonly int[] PayloadSizeByType = BuildPayloadSizes();

    public static bool TryGetPayloadSize(byte eventType, out int payloadSize)
    {
        payloadSize = eventType < PayloadSizeByType.Length ? PayloadSizeByType[eventType] : -1;
        return payloadSize >= 0;
    }

    private static int[] BuildPayloadSizes()
    {
        var sizes = new int[33];
        Array.Fill(sizes, -1);

        sizes[(int)EventType.TruePosition] = 6;
        sizes[(int)EventType.TrueRotation] = 4;
        sizes[(int)EventType.TrueRotationSingleAxis] = 2;
        sizes[(int)EventType.TrueScale] = 6;
        sizes[(int)EventType.TrueUniformScale] = 2;
        sizes[(int)EventType.DeltaPosition] = 4;
        sizes[(int)EventType.DeltaRotation] = 3;
        sizes[(int)EventType.DeltaRotationSingleAxis] = 2;
        sizes[(int)EventType.DeltaScale] = 3;
        sizes[(int)EventType.DeltaUniformScale] = 1;
        sizes[(int)EventType.TrueTransform] = 16;
        sizes[(int)EventType.TruePositionRotation] = 10;
        sizes[(int)EventType.TrueRotationScale] = 10;
        sizes[(int)EventType.TruePositionScale] = 12;
        sizes[(int)EventType.TruePositionRotationUniformScale] = 12;
        sizes[(int)EventType.TruePositionRotationSingleAxis] = 8;
        sizes[(int)EventType.DeltaTransform] = 10;
        sizes[(int)EventType.DeltaPositionRotation] = 7;
        sizes[(int)EventType.DeltaRotationScale] = 6;
        sizes[(int)EventType.DeltaPositionScale] = 7;
        sizes[(int)EventType.DeltaPositionRotationSingleAxis] = 6;
        sizes[(int)EventType.DeltaPositionUniformScale] = 5;
        sizes[(int)EventType.DeltaRotationUniformScale] = 4;
        sizes[(int)EventType.ShowObject] = 0;
        sizes[(int)EventType.HideObject] = 0;
        sizes[(int)EventType.InstantiateObject] = 20; // template(2) + parent(2) + pos(6) + rot(4) + scale(6)
        sizes[(int)EventType.DeleteObject] = 0;
        sizes[(int)EventType.ReparentObject] = 18;    // parent(2) + pos(6) + rot(4) + scale(6)
        return sizes;
    }
}
