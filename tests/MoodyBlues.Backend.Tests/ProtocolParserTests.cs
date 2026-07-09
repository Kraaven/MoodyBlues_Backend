using System.Numerics;
using MoodyBlues.Backend.Protocol;
using Xunit;
using static MoodyBlues.Backend.Tests.EncodingHelpers;

namespace MoodyBlues.Backend.Tests;

/// <summary>
/// Round-trip / known-vector tests for <see cref="EventParser"/>.
///
/// Each test builds a hand-crafted binary fixture (mirroring what the Unity
/// client would write per Spec.md) using the test-only encoders in
/// <see cref="EncodingHelpers"/>, then feeds it through the real decoder and
/// checks the decoded values are correct within quantization tolerance.
/// </summary>
public class ProtocolParserTests
{
    private static readonly Quaternion Identity = Quaternion.Identity;

    private static void AssertVec3Close(Vector3 expected, Vector3 actual, float tol = 0.01f)
    {
        Assert.True(MathF.Abs(expected.X - actual.X) <= tol, $"X: expected {expected.X}, got {actual.X}");
        Assert.True(MathF.Abs(expected.Y - actual.Y) <= tol, $"Y: expected {expected.Y}, got {actual.Y}");
        Assert.True(MathF.Abs(expected.Z - actual.Z) <= tol, $"Z: expected {expected.Z}, got {actual.Z}");
    }

    private static void AssertQuatClose(Quaternion expected, Quaternion actual, float tol = 0.01f)
    {
        Assert.True(MathF.Abs(expected.X - actual.X) <= tol, $"qX: expected {expected.X}, got {actual.X}");
        Assert.True(MathF.Abs(expected.Y - actual.Y) <= tol, $"qY: expected {expected.Y}, got {actual.Y}");
        Assert.True(MathF.Abs(expected.Z - actual.Z) <= tol, $"qZ: expected {expected.Z}, got {actual.Z}");
        Assert.True(MathF.Abs(expected.W - actual.W) <= tol, $"qW: expected {expected.W}, got {actual.W}");
    }

    [Fact]
    public void TimeStamp_Decodes()
    {
        byte[] data = TimeStampBytes(123.456);
        var events = EventParser.ParseMessage(data);

        Assert.Single(events);
        var ts = Assert.IsType<TimeStampEvent>(events[0]);
        Assert.Equal(123.456, ts.Seconds, 6);
    }

    [Fact]
    public void ShowHideDeleteObject_Decode()
    {
        byte[] data = Concat(
            Envelope(EventType.ShowObject, 7),
            Envelope(EventType.HideObject, 8),
            Envelope(EventType.DeleteObject, 9));

        var events = EventParser.ParseMessage(data);

        Assert.Equal(3, events.Count);
        Assert.Equal(7, Assert.IsType<ShowObjectEvent>(events[0]).ObjectId);
        Assert.Equal(8, Assert.IsType<HideObjectEvent>(events[1]).ObjectId);
        Assert.Equal(9, Assert.IsType<DeleteObjectEvent>(events[2]).ObjectId);
    }

    [Fact]
    public void TruePosition_Decode()
    {
        var pos = new Vector3(12.5f, -3.25f, 400.0f);
        byte[] data = Concat(Envelope(EventType.TruePosition, 42), PackVector3_16(pos, ProtocolConstants.RangePosition));

        var events = EventParser.ParseMessage(data);
        var evt = Assert.IsType<TruePositionEvent>(Assert.Single(events));

        Assert.Equal(42, evt.ObjectId);
        AssertVec3Close(pos, evt.Position);
    }

    [Fact]
    public void TrueScaleAndUniformScale_Decode()
    {
        var scale = new Vector3(2.0f, 2.0f, 2.0f);
        byte[] data = Concat(
            Envelope(EventType.TrueScale, 1), PackVector3_16(scale, ProtocolConstants.RangeScaleTrue),
            Envelope(EventType.TrueUniformScale, 1), PackScalar16(2.0f, ProtocolConstants.RangeScaleTrue));

        var events = EventParser.ParseMessage(data);
        var trueScale = Assert.IsType<TrueScaleEvent>(events[0]);
        var uniformScale = Assert.IsType<TrueUniformScaleEvent>(events[1]);

        AssertVec3Close(scale, trueScale.Scale);
        Assert.True(MathF.Abs(uniformScale.Scale - 2.0f) <= 0.01f);
    }

    [Fact]
    public void QuatSmallestThree_Identity_Decode()
    {
        byte[] data = Concat(Envelope(EventType.TrueRotation, 5), PackQuatSmallestThree(Identity, ProtocolConstants.RangeQuatSmallestThree));

        var events = EventParser.ParseMessage(data);
        var evt = Assert.IsType<TrueRotationEvent>(Assert.Single(events));

        AssertQuatClose(Identity, evt.Rotation, 0.005f);
    }

    [Fact]
    public void QuatSmallestThree_Arbitrary_Decode()
    {
        // 90 degree rotation around Y: (0, sin(45deg), 0, cos(45deg))
        float half = MathF.Sqrt(2f) / 2f;
        var quat = new Quaternion(0f, half, 0f, half);
        byte[] data = Concat(Envelope(EventType.TrueRotation, 5), PackQuatSmallestThree(quat, ProtocolConstants.RangeQuatSmallestThree));

        var events = EventParser.ParseMessage(data);
        var evt = Assert.IsType<TrueRotationEvent>(Assert.Single(events));

        AssertQuatClose(quat, evt.Rotation, 0.005f);
    }

    [Fact]
    public void QuatDropW_DeltaRotation_Decode()
    {
        float w = MathF.Sqrt(1f - 0.1f * 0.1f - 0.2f * 0.2f - 0.05f * 0.05f);
        var quat = new Quaternion(0.1f, -0.2f, 0.05f, w);
        byte[] data = Concat(Envelope(EventType.DeltaRotation, 3), PackQuatDropW(quat, ProtocolConstants.RangeQuatDropW));

        var events = EventParser.ParseMessage(data);
        var evt = Assert.IsType<DeltaRotationEvent>(Assert.Single(events));

        AssertQuatClose(quat, evt.DeltaRotation, 0.01f);
    }

    [Fact]
    public void AxisAngle_TrueRotationSingleAxis_Decode()
    {
        byte[] data = Concat(Envelope(EventType.TrueRotationSingleAxis, 11), PackAxisAngle(1, 180.0f, ProtocolConstants.RangeAngleTrueDeg));

        var events = EventParser.ParseMessage(data);
        var evt = Assert.IsType<TrueRotationSingleAxisEvent>(Assert.Single(events));

        Assert.Equal("Y", evt.Axis);
        Assert.True(MathF.Abs(evt.AngleDeg - 180.0f) <= 0.05f);
    }

    [Fact]
    public void AxisAngle_DeltaRotationSingleAxis_Decode()
    {
        byte[] data = Concat(Envelope(EventType.DeltaRotationSingleAxis, 11), PackAxisAngle(2, -1.5f, ProtocolConstants.RangeAngleDeltaDeg));

        var events = EventParser.ParseMessage(data);
        var evt = Assert.IsType<DeltaRotationSingleAxisEvent>(Assert.Single(events));

        Assert.Equal("Z", evt.Axis);
        Assert.True(MathF.Abs(evt.DeltaAngleDeg - -1.5f) <= 0.01f);
    }

    [Fact]
    public void DeltaPositionPacked_Decode()
    {
        var delta = new Vector3(0.5f, -0.75f, 1.0f);
        byte[] data = Concat(Envelope(EventType.DeltaPosition, 20), PackDeltaPosition(delta, ProtocolConstants.RangeDeltaPosition));

        var events = EventParser.ParseMessage(data);
        var evt = Assert.IsType<DeltaPositionEvent>(Assert.Single(events));

        AssertVec3Close(delta, evt.DeltaPosition, 0.005f);
    }

    [Fact]
    public void DeltaScaleAndUniformScale_Decode()
    {
        var deltaScale = new Vector3(0.1f, -0.1f, 0.0f);
        byte[] data = Concat(
            Envelope(EventType.DeltaScale, 1), PackVector3_8(deltaScale, ProtocolConstants.RangeScaleDelta),
            Envelope(EventType.DeltaUniformScale, 1), PackScalar8(-0.3f, ProtocolConstants.RangeScaleDelta));

        var events = EventParser.ParseMessage(data);
        var deltaScaleEvt = Assert.IsType<DeltaScaleEvent>(events[0]);
        var deltaUniformEvt = Assert.IsType<DeltaUniformScaleEvent>(events[1]);

        AssertVec3Close(deltaScale, deltaScaleEvt.DeltaScale, 0.02f);
        Assert.True(MathF.Abs(deltaUniformEvt.DeltaScale - -0.3f) <= 0.02f);
    }

    [Fact]
    public void InstantiateObject_Decode()
    {
        ushort templateId = 3;
        var position = new Vector3(1.0f, 2.0f, 3.0f);
        var scale = new Vector3(1.0f, 1.0f, 1.0f);

        byte[] payload = Concat(
            BitConverter.GetBytes(templateId),
            PackVector3_16(position, ProtocolConstants.RangePosition),
            PackQuatSmallestThree(Identity, ProtocolConstants.RangeQuatSmallestThree),
            PackVector3_16(scale, ProtocolConstants.RangeScaleTrue));

        byte[] data = Concat(Envelope(EventType.InstantiateObject, 500), payload);

        var events = EventParser.ParseMessage(data);
        var evt = Assert.IsType<InstantiateObjectEvent>(Assert.Single(events));

        Assert.Equal(500, evt.ObjectId);
        Assert.Equal(templateId, evt.TemplateObjectId);
        AssertVec3Close(position, evt.Position);
        AssertQuatClose(Identity, evt.Rotation, 0.005f);
        AssertVec3Close(scale, evt.Scale);
    }

    [Fact]
    public void TrueTransformCombined_Decode()
    {
        var position = new Vector3(10.0f, 20.0f, -30.0f);
        var scale = new Vector3(1.0f, 1.0f, 1.0f);

        byte[] payload = Concat(
            PackVector3_16(position, ProtocolConstants.RangePosition),
            PackQuatSmallestThree(Identity, ProtocolConstants.RangeQuatSmallestThree),
            PackVector3_16(scale, ProtocolConstants.RangeScaleTrue));

        byte[] data = Concat(Envelope(EventType.TrueTransform, 1), payload);

        var events = EventParser.ParseMessage(data);
        var evt = Assert.IsType<TrueTransformEvent>(Assert.Single(events));

        AssertVec3Close(position, evt.Position);
        AssertQuatClose(Identity, evt.Rotation, 0.005f);
        AssertVec3Close(scale, evt.Scale);
    }

    [Fact]
    public void MultiEventMessage_OrderPreserved()
    {
        byte[] data = Concat(
            TimeStampBytes(1.0),
            Envelope(EventType.ShowObject, 1),
            Envelope(EventType.TruePosition, 1),
            PackVector3_16(Vector3.Zero, ProtocolConstants.RangePosition),
            Envelope(EventType.HideObject, 1));

        var events = EventParser.ParseMessage(data);

        Assert.Collection(
            events,
            e => Assert.IsType<TimeStampEvent>(e),
            e => Assert.IsType<ShowObjectEvent>(e),
            e => Assert.IsType<TruePositionEvent>(e),
            e => Assert.IsType<HideObjectEvent>(e));
    }

    [Fact]
    public void UnknownEventType_Throws()
    {
        Assert.Throws<ProtocolException>(() => EventParser.ParseMessage([200, 0, 0]));
    }

    [Fact]
    public void TruncatedPayload_Throws()
    {
        byte[] data = Concat(Envelope(EventType.TruePosition, 1), [0, 0]);
        Assert.Throws<ProtocolException>(() => EventParser.ParseMessage(data));
    }

    [Fact]
    public void TruncatedTimeStamp_Throws()
    {
        Assert.Throws<ProtocolException>(() => EventParser.ParseMessage([(byte)EventType.TimeStamp, 0, 0]));
    }

    [Fact]
    public void EmptyMessage_ReturnsEmptyList()
    {
        Assert.Empty(EventParser.ParseMessage([]));
    }
}
