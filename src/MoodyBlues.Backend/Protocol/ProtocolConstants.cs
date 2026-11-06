namespace MoodyBlues.Backend.Protocol;

/// <summary>
/// Fixed sizes and constants from the Unity repo's Spec.md (Sections 4, 6 and 12). Every value
/// here is traceable to that document; if they ever disagree, the Unity code is the source of
/// truth and this file is stale.
/// </summary>
public static class ProtocolConstants
{
    /// <summary>The only protocol version this decoder implements (Spec 6.1, BeginSession).</summary>
    public const ushort ProtocolVersion = 1;

    /// <summary>EventType + ObjectID, carried by every event except the session maintenance ones.</summary>
    public const int EnvelopeSize = 3;

    public const int TimeStampSize = 9;
    public const int BeginSessionSize = 35;
    public const int KeepAliveSize = 1;
    public const int EndSessionSize = 18;

    /// <summary>Largest single event (BeginSession).</summary>
    public const int MaxEventSize = BeginSessionSize;

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
