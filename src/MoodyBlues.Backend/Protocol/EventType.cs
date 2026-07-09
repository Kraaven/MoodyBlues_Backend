namespace MoodyBlues.Backend.Protocol;

/// <summary>
/// Wire event type byte values (Spec.md Section 5).
/// </summary>
public enum EventType : byte
{
    // 5.2 Single-property events
    TruePosition = 1,
    TrueRotation = 2,
    TrueRotationSingleAxis = 3,
    TrueScale = 4,
    TrueUniformScale = 5,
    DeltaPosition = 6,
    DeltaRotation = 7,
    DeltaRotationSingleAxis = 8,
    DeltaScale = 9,
    DeltaUniformScale = 10,

    // 5.3 Combined "hot path" events
    TrueTransform = 11,
    TruePositionRotation = 12,
    TrueRotationScale = 13,
    TruePositionScale = 14,
    TruePositionRotationUniformScale = 15,
    TruePositionRotationSingleAxis = 16,
    DeltaTransform = 17,
    DeltaPositionRotation = 18,
    DeltaRotationScale = 19,
    DeltaPositionScale = 20,
    DeltaPositionRotationSingleAxis = 21,
    DeltaPositionUniformScale = 22,
    DeltaRotationUniformScale = 23,

    // 5.1 Session / lifecycle events
    TimeStamp = 24,
    ShowObject = 25,
    HideObject = 26,
    InstantiateObject = 27,
    DeleteObject = 28,
}
