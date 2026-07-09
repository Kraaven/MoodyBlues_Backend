using System.Globalization;
using System.Numerics;

namespace MoodyBlues.Backend.Protocol;

/// <summary>Human-readable formatting of decoded events, for detailed logging.</summary>
public static class EventFormatting
{
    private static string FmtVec3(Vector3 v) =>
        string.Format(CultureInfo.InvariantCulture, "({0:F4}, {1:F4}, {2:F4})", v.X, v.Y, v.Z);

    private static string FmtQuat(Quaternion q) =>
        string.Format(CultureInfo.InvariantCulture, "(x={0:F4}, y={1:F4}, z={2:F4}, w={3:F4})", q.X, q.Y, q.Z, q.W);

    /// <summary>One-line, human-readable description of a decoded event.</summary>
    public static string Format(MoodyEvent evt)
    {
        string oid = evt is IObjectEvent objEvt ? $"obj={objEvt.ObjectId}" : string.Empty;

        return evt switch
        {
            TimeStampEvent e => $"TimeStamp seconds={e.Seconds:F6}",
            ShowObjectEvent => $"ShowObject {oid}",
            HideObjectEvent => $"HideObject {oid}",
            DeleteObjectEvent => $"DeleteObject {oid}",
            InstantiateObjectEvent e =>
                $"InstantiateObject {oid} template={e.TemplateObjectId} pos={FmtVec3(e.Position)} " +
                $"rot={FmtQuat(e.Rotation)} scale={FmtVec3(e.Scale)}",

            TruePositionEvent e => $"TruePosition {oid} pos={FmtVec3(e.Position)}",
            TrueRotationEvent e => $"TrueRotation {oid} rot={FmtQuat(e.Rotation)}",
            TrueRotationSingleAxisEvent e => $"TrueRotationSingleAxis {oid} axis={e.Axis} angle={e.AngleDeg:F2}deg",
            TrueScaleEvent e => $"TrueScale {oid} scale={FmtVec3(e.Scale)}",
            TrueUniformScaleEvent e => $"TrueUniformScale {oid} scale={e.Scale:F4}",

            DeltaPositionEvent e => $"DeltaPosition {oid} delta={FmtVec3(e.DeltaPosition)}",
            DeltaRotationEvent e => $"DeltaRotation {oid} delta={FmtQuat(e.DeltaRotation)}",
            DeltaRotationSingleAxisEvent e => $"DeltaRotationSingleAxis {oid} axis={e.Axis} delta={e.DeltaAngleDeg:F3}deg",
            DeltaScaleEvent e => $"DeltaScale {oid} delta={FmtVec3(e.DeltaScale)}",
            DeltaUniformScaleEvent e => $"DeltaUniformScale {oid} delta={e.DeltaScale:F4}",

            TrueTransformEvent e =>
                $"TrueTransform {oid} pos={FmtVec3(e.Position)} rot={FmtQuat(e.Rotation)} scale={FmtVec3(e.Scale)}",
            TruePositionRotationEvent e =>
                $"TruePositionRotation {oid} pos={FmtVec3(e.Position)} rot={FmtQuat(e.Rotation)}",
            TrueRotationScaleEvent e =>
                $"TrueRotationScale {oid} rot={FmtQuat(e.Rotation)} scale={FmtVec3(e.Scale)}",
            TruePositionScaleEvent e =>
                $"TruePositionScale {oid} pos={FmtVec3(e.Position)} scale={FmtVec3(e.Scale)}",
            TruePositionRotationUniformScaleEvent e =>
                $"TruePositionRotationUniformScale {oid} pos={FmtVec3(e.Position)} rot={FmtQuat(e.Rotation)} scale={e.UniformScale:F4}",
            TruePositionRotationSingleAxisEvent e =>
                $"TruePositionRotationSingleAxis {oid} pos={FmtVec3(e.Position)} axis={e.Axis} angle={e.AngleDeg:F2}deg",

            DeltaTransformEvent e =>
                $"DeltaTransform {oid} dpos={FmtVec3(e.DeltaPosition)} drot={FmtQuat(e.DeltaRotation)} dscale={FmtVec3(e.DeltaScale)}",
            DeltaPositionRotationEvent e =>
                $"DeltaPositionRotation {oid} dpos={FmtVec3(e.DeltaPosition)} drot={FmtQuat(e.DeltaRotation)}",
            DeltaRotationScaleEvent e =>
                $"DeltaRotationScale {oid} drot={FmtQuat(e.DeltaRotation)} dscale={FmtVec3(e.DeltaScale)}",
            DeltaPositionScaleEvent e =>
                $"DeltaPositionScale {oid} dpos={FmtVec3(e.DeltaPosition)} dscale={FmtVec3(e.DeltaScale)}",
            DeltaPositionRotationSingleAxisEvent e =>
                $"DeltaPositionRotationSingleAxis {oid} dpos={FmtVec3(e.DeltaPosition)} axis={e.Axis} delta={e.DeltaAngleDeg:F3}deg",
            DeltaPositionUniformScaleEvent e =>
                $"DeltaPositionUniformScale {oid} dpos={FmtVec3(e.DeltaPosition)} dscale={e.DeltaUniformScale:F4}",
            DeltaRotationUniformScaleEvent e =>
                $"DeltaRotationUniformScale {oid} drot={FmtQuat(e.DeltaRotation)} dscale={e.DeltaUniformScale:F4}",

            _ => $"{evt.GetType().Name} {oid} {evt}",
        };
    }
}
