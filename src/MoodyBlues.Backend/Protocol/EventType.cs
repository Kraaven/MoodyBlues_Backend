namespace MoodyBlues.Backend.Protocol;

/// <summary>
/// Wire event type byte values. Mirrors <c>Serializer.EventType</c> in the Unity client and
/// Section 6 of the Unity repo's Spec.md. 0 is never a valid type (it is the client ring buffer's
/// padding marker and never reaches the wire).
/// </summary>
public enum EventType : byte
{
    // Single-property transform events (Spec 6.3)
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

    // Combined transform events (Spec 6.4)
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

    // Stream / lifecycle events (Spec 6.1, 6.2)
    TimeStamp = 24,
    ShowObject = 25,
    HideObject = 26,
    InstantiateObject = 27,
    DeleteObject = 28,
    BeginSession = 29,
    KeepAlive = 30,
    EndSession = 31,
    ReparentObject = 32,
}

/// <summary>Why a session ended, carried in <c>EndSession</c> (Spec 6.1).</summary>
public enum SessionEndReason : byte
{
    StoppedByHost = 0,
    ApplicationQuit = 1,
    SceneUnloaded = 2,
    ClientError = 3,
}
