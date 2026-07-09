namespace MoodyBlues.Backend.Protocol;

/// <summary>
/// Event sizes and quantization ranges from Spec.md. Every constant here is
/// traceable to a section of Spec.md (in the Unity project) -- keep the two
/// files in sync if the wire format ever changes.
/// </summary>
public static class ProtocolConstants
{
    /// <summary>Envelope size (EventType + ObjectID) for every event except TimeStamp.</summary>
    public const int EnvelopeSize = 3;

    /// <summary>TimeStamp is the only event with no envelope: 1 byte EventType + 8 byte double.</summary>
    public const int TimestampSize = 9;

    /// <summary>Max single event size (InstantiateObject), Spec.md Section 8.</summary>
    public const int MaxEventSize = 21;

    /// <summary>
    /// Payload size in bytes (NOT including the 3-byte envelope) for every
    /// envelope-carrying event. Spec.md Section 5's "Total" column minus 3.
    /// </summary>
    public static readonly IReadOnlyDictionary<EventType, int> PayloadSizes = new Dictionary<EventType, int>
    {
        [EventType.TruePosition] = 6,
        [EventType.TrueRotation] = 4,
        [EventType.TrueRotationSingleAxis] = 2,
        [EventType.TrueScale] = 6,
        [EventType.TrueUniformScale] = 2,
        [EventType.DeltaPosition] = 4,
        [EventType.DeltaRotation] = 3,
        [EventType.DeltaRotationSingleAxis] = 2,
        [EventType.DeltaScale] = 3,
        [EventType.DeltaUniformScale] = 1,
        [EventType.TrueTransform] = 16,
        [EventType.TruePositionRotation] = 10,
        [EventType.TrueRotationScale] = 10,
        [EventType.TruePositionScale] = 12,
        [EventType.TruePositionRotationUniformScale] = 12,
        [EventType.TruePositionRotationSingleAxis] = 8,
        [EventType.DeltaTransform] = 10,
        [EventType.DeltaPositionRotation] = 7,
        [EventType.DeltaRotationScale] = 6,
        [EventType.DeltaPositionScale] = 7,
        [EventType.DeltaPositionRotationSingleAxis] = 6,
        [EventType.DeltaPositionUniformScale] = 5,
        [EventType.DeltaRotationUniformScale] = 4,
        [EventType.ShowObject] = 0,
        [EventType.HideObject] = 0,
        // TemplateObjectID(2) + Position(6) + Rotation(4) + Scale(6)
        [EventType.InstantiateObject] = 18,
        [EventType.DeleteObject] = 0,
    };

    // Quantization ranges (Spec.md Section 7), as (Min, Max) float tuples.
    public static readonly (float Min, float Max) RangePosition = (-500f, 500f);
    public static readonly (float Min, float Max) RangeScaleTrue = (-1f, 10f);
    public static readonly (float Min, float Max) RangeScaleDelta = (-1f, 1f);
    public static readonly (float Min, float Max) RangeQuatSmallestThree = (-0.70711f, 0.70711f);
    public static readonly (float Min, float Max) RangeQuatDropW = (-0.5f, 0.5f);
    public static readonly (float Min, float Max) RangeAngleTrueDeg = (0f, 360f);
    public static readonly (float Min, float Max) RangeAngleDeltaDeg = (-2f, 2f);
    public static readonly (float Min, float Max) RangeDeltaPosition = (-1.5f, 1.5f);

    public static readonly string[] AxisNames = ["X", "Y", "Z"];
}
