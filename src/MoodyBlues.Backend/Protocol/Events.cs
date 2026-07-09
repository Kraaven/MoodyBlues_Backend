using System.Numerics;

namespace MoodyBlues.Backend.Protocol;

/// <summary>Base type for every decoded event, one record per Spec.md Section 5 event type.</summary>
public abstract record MoodyEvent;

/// <summary>Implemented by every event except <see cref="TimeStampEvent"/>, the only one with no ObjectID.</summary>
public interface IObjectEvent
{
    ushort ObjectId { get; }
}

// --- 5.1 Session / lifecycle events -----------------------------------------

public sealed record TimeStampEvent(double Seconds) : MoodyEvent;

public sealed record ShowObjectEvent(ushort ObjectId) : MoodyEvent, IObjectEvent;

public sealed record HideObjectEvent(ushort ObjectId) : MoodyEvent, IObjectEvent;

public sealed record InstantiateObjectEvent(
    ushort ObjectId,
    ushort TemplateObjectId,
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Scale) : MoodyEvent, IObjectEvent;

public sealed record DeleteObjectEvent(ushort ObjectId) : MoodyEvent, IObjectEvent;

// --- 5.2 Single-property events ---------------------------------------------

public sealed record TruePositionEvent(ushort ObjectId, Vector3 Position) : MoodyEvent, IObjectEvent;

public sealed record TrueRotationEvent(ushort ObjectId, Quaternion Rotation) : MoodyEvent, IObjectEvent;

public sealed record TrueRotationSingleAxisEvent(ushort ObjectId, string Axis, float AngleDeg) : MoodyEvent, IObjectEvent;

public sealed record TrueScaleEvent(ushort ObjectId, Vector3 Scale) : MoodyEvent, IObjectEvent;

public sealed record TrueUniformScaleEvent(ushort ObjectId, float Scale) : MoodyEvent, IObjectEvent;

public sealed record DeltaPositionEvent(ushort ObjectId, Vector3 DeltaPosition) : MoodyEvent, IObjectEvent;

public sealed record DeltaRotationEvent(ushort ObjectId, Quaternion DeltaRotation) : MoodyEvent, IObjectEvent;

public sealed record DeltaRotationSingleAxisEvent(ushort ObjectId, string Axis, float DeltaAngleDeg) : MoodyEvent, IObjectEvent;

public sealed record DeltaScaleEvent(ushort ObjectId, Vector3 DeltaScale) : MoodyEvent, IObjectEvent;

public sealed record DeltaUniformScaleEvent(ushort ObjectId, float DeltaScale) : MoodyEvent, IObjectEvent;

// --- 5.3 Combined "hot path" events -----------------------------------------

public sealed record TrueTransformEvent(ushort ObjectId, Vector3 Position, Quaternion Rotation, Vector3 Scale) : MoodyEvent, IObjectEvent;

public sealed record TruePositionRotationEvent(ushort ObjectId, Vector3 Position, Quaternion Rotation) : MoodyEvent, IObjectEvent;

public sealed record TrueRotationScaleEvent(ushort ObjectId, Quaternion Rotation, Vector3 Scale) : MoodyEvent, IObjectEvent;

public sealed record TruePositionScaleEvent(ushort ObjectId, Vector3 Position, Vector3 Scale) : MoodyEvent, IObjectEvent;

public sealed record TruePositionRotationUniformScaleEvent(
    ushort ObjectId,
    Vector3 Position,
    Quaternion Rotation,
    float UniformScale) : MoodyEvent, IObjectEvent;

public sealed record TruePositionRotationSingleAxisEvent(
    ushort ObjectId,
    Vector3 Position,
    string Axis,
    float AngleDeg) : MoodyEvent, IObjectEvent;

public sealed record DeltaTransformEvent(
    ushort ObjectId,
    Vector3 DeltaPosition,
    Quaternion DeltaRotation,
    Vector3 DeltaScale) : MoodyEvent, IObjectEvent;

public sealed record DeltaPositionRotationEvent(ushort ObjectId, Vector3 DeltaPosition, Quaternion DeltaRotation) : MoodyEvent, IObjectEvent;

public sealed record DeltaRotationScaleEvent(ushort ObjectId, Quaternion DeltaRotation, Vector3 DeltaScale) : MoodyEvent, IObjectEvent;

public sealed record DeltaPositionScaleEvent(ushort ObjectId, Vector3 DeltaPosition, Vector3 DeltaScale) : MoodyEvent, IObjectEvent;

public sealed record DeltaPositionRotationSingleAxisEvent(
    ushort ObjectId,
    Vector3 DeltaPosition,
    string Axis,
    float DeltaAngleDeg) : MoodyEvent, IObjectEvent;

public sealed record DeltaPositionUniformScaleEvent(
    ushort ObjectId,
    Vector3 DeltaPosition,
    float DeltaUniformScale) : MoodyEvent, IObjectEvent;

public sealed record DeltaRotationUniformScaleEvent(
    ushort ObjectId,
    Quaternion DeltaRotation,
    float DeltaUniformScale) : MoodyEvent, IObjectEvent;
