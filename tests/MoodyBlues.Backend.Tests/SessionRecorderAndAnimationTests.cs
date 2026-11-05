using System.Numerics;
using MoodyBlues.Backend.Protocol;
using MoodyBlues.Backend.Scenes.Processing;
using MoodyBlues.Backend.Session;
using Xunit;

namespace MoodyBlues.Backend.Tests;

/// <summary>
/// Covers <see cref="SessionRecorder"/> (event -> running per-object state), <see cref="AnimationBuilder"/>
/// (timeline -> per-channel samples), and <see cref="AnimationOptimizer"/> (key reduction / constant-linear
/// elimination / quantization).
/// </summary>
public class SessionRecorderAndAnimationTests
{
    [Fact]
    public void Recorder_DeltaPosition_AccumulatesOnTopOfPreviousState()
    {
        var recorder = new SessionRecorder();

        recorder.Record(new TimeStampEvent(1.0));
        recorder.Record(new TruePositionEvent(1, new Vector3(10, 0, 0)));
        recorder.Record(new TimeStampEvent(2.0));
        recorder.Record(new DeltaPositionEvent(1, new Vector3(1, 2, 3)));

        SessionTimeline timeline = recorder.Finalize();

        Assert.Equal(2, timeline.Samples.Count);
        Assert.Equal(new Vector3(10, 0, 0), timeline.Samples[0].Position);
        Assert.Equal(new Vector3(11, 2, 3), timeline.Samples[1].Position);
        Assert.Equal(2.0, timeline.Samples[1].Time);
        Assert.Equal(4, timeline.RawEventCount); // TimeStamp events count too.
    }

    [Fact]
    public void Recorder_DeltaRotation_ComposesOnTopOfPreviousRotation()
    {
        var recorder = new SessionRecorder();

        // 90 degrees about Y, then another 90 degrees about Y via delta -- should end up at 180.
        var ninetyY = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        recorder.Record(new TrueRotationEvent(1, ninetyY));
        recorder.Record(new DeltaRotationEvent(1, ninetyY));

        SessionTimeline timeline = recorder.Finalize();
        Quaternion result = timeline.Samples[^1].Rotation;

        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);
        Assert.True(Quaternion.Dot(Quaternion.Normalize(result), expected) > 0.999f);
    }

    [Fact]
    public void Recorder_InstantiateObject_SetsTemplateIdAndInitialTransform()
    {
        var recorder = new SessionRecorder();
        var pos = new Vector3(1, 2, 3);
        var rot = Quaternion.Identity;
        var scale = Vector3.One;

        recorder.Record(new InstantiateObjectEvent(42, 7, 0, pos, rot, scale));

        SessionTimeline timeline = recorder.Finalize();

        Assert.Equal(42, Assert.Single(timeline.ObjectOrder));
        Assert.Equal(7, timeline.TemplateObjectIdByObjectId[42]);
        Assert.Equal(LifecycleEventType.Spawn, timeline.Samples[0].Lifecycle);
        Assert.Equal(pos, timeline.Samples[0].Position);
    }

    [Fact]
    public void Recorder_HideThenShow_RecordsLifecycleTransitions()
    {
        var recorder = new SessionRecorder();
        recorder.Record(new TruePositionEvent(5, Vector3.Zero));
        recorder.Record(new HideObjectEvent(5));
        recorder.Record(new ShowObjectEvent(5));

        SessionTimeline timeline = recorder.Finalize();

        Assert.Equal(LifecycleEventType.Hide, timeline.Samples[1].Lifecycle);
        Assert.False(timeline.Samples[1].Visible);
        Assert.Equal(LifecycleEventType.Show, timeline.Samples[2].Lifecycle);
        Assert.True(timeline.Samples[2].Visible);
    }

    [Fact]
    public void AnimationBuilder_SplitsSamplesIntoPerObjectPerChannelPoints()
    {
        var recorder = new SessionRecorder();
        recorder.Record(new TimeStampEvent(0));
        recorder.Record(new TruePositionEvent(1, new Vector3(1, 2, 3)));
        recorder.Record(new TimeStampEvent(1));
        recorder.Record(new TruePositionEvent(1, new Vector3(4, 5, 6)));

        List<RawObjectTimeline> objects = AnimationBuilder.Build(recorder.Finalize());

        RawObjectTimeline obj = Assert.Single(objects);
        Assert.Equal(1, obj.ObjectId);
        Assert.Null(obj.TemplateObjectId);
        Assert.Equal(2, obj.PosX.Count);
        Assert.Equal((0.0, 1f), obj.PosX[0]);
        Assert.Equal((1.0, 4f), obj.PosX[1]);
        Assert.Equal((0.0, 3f), obj.PosZ[0]);
    }

    [Fact]
    public void AnimationOptimizer_ConstantChannel_CollapsesToSingleValue()
    {
        var points = new List<(double Time, float Value)> { (0, 5f), (1, 5f), (2, 5.0000001f), (3, 5f) };
        AnimationFileDto file = OptimizeSingleChannel(points, out string key);

        AnimationChannelDto channel = file.Objects[0].Channels[key];
        Assert.Equal("constant", channel.Type);
        Assert.Equal(5f, channel.Value);
    }

    [Fact]
    public void AnimationOptimizer_StraightLine_CollapsesToLinear()
    {
        var points = Enumerable.Range(0, 50).Select(i => ((double)i, (float)i)).ToList();
        AnimationFileDto file = OptimizeSingleChannel(points, out string key);

        AnimationChannelDto channel = file.Objects[0].Channels[key];
        Assert.Equal("linear", channel.Type);
        Assert.Equal(0.0, channel.T0);
        Assert.Equal(0f, channel.V0);
        Assert.Equal(49.0, channel.T1);
        Assert.Equal(49f, channel.V1);
    }

    [Fact]
    public void AnimationOptimizer_NoisyCurve_KeepsKeyframesAndQuantizes()
    {
        // A curve with a sharp corner in the middle -- Douglas-Peucker must keep the corner.
        var points = new List<(double Time, float Value)>();
        for (int i = 0; i <= 10; i++) points.Add((i, i));
        for (int i = 11; i <= 20; i++) points.Add((i, 20 - i));

        AnimationFileDto file = OptimizeSingleChannel(points, out string key);
        AnimationChannelDto channel = file.Objects[0].Channels[key];

        Assert.Equal("keyframes", channel.Type);
        Assert.NotNull(channel.Values);
        // Massively fewer than the 21 original points, but not collapsed to a single line.
        Assert.True(channel.Values!.Length < points.Count);
        Assert.True(channel.Values.Length >= 3);
    }

    [Fact]
    public void DouglasPeucker_KeepsEndpointsAndSignificantCorner()
    {
        var points = new List<(double Time, float Value)> { (0, 0f), (1, 0f), (2, 10f), (3, 0f), (4, 0f) };
        var simplified = DouglasPeucker.Simplify(points, epsilon: 0.5);

        Assert.Equal((0.0, 0f), simplified[0]);
        Assert.Equal((4.0, 0f), simplified[^1]);
        Assert.Contains((2.0, 10f), simplified);
    }

    [Fact]
    public void DouglasPeucker_FlatLine_ReducesToEndpointsOnly()
    {
        var points = Enumerable.Range(0, 20).Select(i => ((double)i, 3f)).ToList();
        var simplified = DouglasPeucker.Simplify(points, epsilon: 0.01);

        Assert.Equal(2, simplified.Count);
    }

    [Fact]
    public void Recorder_StrippedEventLog_DropsTransforms_KeepsLifecycle()
    {
        var recorder = new SessionRecorder();
        var pos = new Vector3(1, 2, 3);

        recorder.Record(new TimeStampEvent(1.0));
        recorder.Record(new TruePositionEvent(1, pos));
        recorder.Record(new TimeStampEvent(2.0));
        recorder.Record(new InstantiateObjectEvent(42, 7, 3, pos, Quaternion.Identity, Vector3.One));
        recorder.Record(new TimeStampEvent(3.0));
        recorder.Record(new DeltaPositionEvent(42, new Vector3(0.1f, 0, 0)));
        recorder.Record(new TimeStampEvent(4.0));
        recorder.Record(new HideObjectEvent(42));

        SessionTimeline timeline = recorder.Finalize();
        Assert.True(timeline.StrippedEventLog.Length > 0);

        List<MoodyEvent> kept = EventParser.ParseMessage(timeline.StrippedEventLog.Span);
        Assert.Equal(4, kept.Count);
        Assert.IsType<TimeStampEvent>(kept[0]);
        Assert.Equal(2.0, ((TimeStampEvent)kept[0]).Seconds);
        Assert.IsType<InstantiateObjectEvent>(kept[1]);
        var inst = (InstantiateObjectEvent)kept[1];
        Assert.Equal(42, inst.ObjectId);
        Assert.Equal(7, inst.TemplateObjectId);
        Assert.Equal(3, inst.ParentObjectId);
        Assert.IsType<TimeStampEvent>(kept[2]);
        Assert.Equal(4.0, ((TimeStampEvent)kept[2]).Seconds);
        Assert.IsType<HideObjectEvent>(kept[3]);
        Assert.Equal(42, ((HideObjectEvent)kept[3]).ObjectId);
    }

    [Fact]
    public void Recorder_StrippedEventLog_PreservesOriginalInstantiateBytes()
    {
        var pos = new Vector3(12.5f, -3.25f, 40f);
        var scale = new Vector3(1.5f, 1.5f, 1.5f);
        Span<byte> wire = stackalloc byte[EventLogEncoder.InstantiateObjectSize];
        int written = EventLogEncoder.EncodeInstantiateObject(
            42, 7, 3, pos, Quaternion.Identity, scale, wire);
        Assert.Equal(EventLogEncoder.InstantiateObjectSize, written);

        byte[] original = wire[..written].ToArray();
        var decoded = (InstantiateObjectEvent)EventParser.ParseMessage(original).Single();

        var recorder = new SessionRecorder();
        recorder.Record(new TimeStampEvent(1.5));
        recorder.Record(decoded, original);

        SessionTimeline timeline = recorder.Finalize();
        // TimeStamp (9) + original instantiate payload — exact wire bytes for the lifecycle event.
        Assert.Equal(9 + original.Length, timeline.StrippedEventLog.Length);
        Assert.True(timeline.StrippedEventLog.Span[9..].SequenceEqual(original));
    }

    [Fact]
    public void EventLogEncoder_RoundTripsLifecycleEvents()
    {
        var pos = new Vector3(1, 2, 3);
        var scale = new Vector3(2, 2, 2);
        Span<byte> buf = stackalloc byte[EventLogEncoder.MaxEventSize];

        int n = EventLogEncoder.EncodeShowObject(11, buf);
        Assert.IsType<ShowObjectEvent>(Assert.Single(EventParser.ParseMessage(buf[..n])));

        n = EventLogEncoder.EncodeHideObject(12, buf);
        Assert.IsType<HideObjectEvent>(Assert.Single(EventParser.ParseMessage(buf[..n])));

        n = EventLogEncoder.EncodeDeleteObject(13, buf);
        Assert.IsType<DeleteObjectEvent>(Assert.Single(EventParser.ParseMessage(buf[..n])));

        n = EventLogEncoder.EncodeInstantiateObject(42, 7, 9, pos, Quaternion.Identity, scale, buf);
        var inst = Assert.IsType<InstantiateObjectEvent>(Assert.Single(EventParser.ParseMessage(buf[..n])));
        Assert.Equal(42, inst.ObjectId);
        Assert.Equal(7, inst.TemplateObjectId);
        Assert.Equal(9, inst.ParentObjectId);
        Assert.True(MathF.Abs(inst.Position.X - pos.X) < 0.02f);
        Assert.True(MathF.Abs(inst.Scale.X - scale.X) < 0.02f);
    }

    [Fact]
    public void AnimationOptimizer_EmitsParentObjectId()
    {
        var recorder = new SessionRecorder();
        recorder.Record(new TimeStampEvent(0));
        recorder.Record(new InstantiateObjectEvent(42, 7, 9, Vector3.One, Quaternion.Identity, Vector3.One));

        SessionTimeline timeline = recorder.Finalize();
        AnimationFileDto file = AnimationOptimizer.Optimize(
            timeline.DurationSeconds,
            AnimationBuilder.Build(timeline));

        AnimationObjectDto obj = Assert.Single(file.Objects);
        Assert.Equal(42, obj.ObjectId);
        Assert.Equal((ushort)7, obj.TemplateObjectId);
        Assert.Equal((ushort)9, obj.ParentObjectId);
    }

    [Fact]
    public void AnimationBuilder_SplitsReusedObjectIdAfterDeleteIntoSeparateInstances()
    {
        var recorder = new SessionRecorder();
        recorder.Record(new TimeStampEvent(0));
        recorder.Record(new InstantiateObjectEvent(5, 1, 0, new Vector3(0, 0, 0), Quaternion.Identity, Vector3.One));
        recorder.Record(new TimeStampEvent(1));
        recorder.Record(new TruePositionEvent(5, new Vector3(10, 0, 0)));
        recorder.Record(new TimeStampEvent(2));
        recorder.Record(new DeleteObjectEvent(5));
        recorder.Record(new TimeStampEvent(3));
        recorder.Record(new InstantiateObjectEvent(5, 2, 0, new Vector3(1, 0, 0), Quaternion.Identity, Vector3.One));
        recorder.Record(new TimeStampEvent(4));
        recorder.Record(new TruePositionEvent(5, new Vector3(20, 0, 0)));

        List<RawObjectTimeline> raw = AnimationBuilder.Build(recorder.Finalize());

        Assert.Equal(2, raw.Count);
        RawObjectTimeline first = raw.Single(o => o.InstanceIndex == 0);
        RawObjectTimeline second = raw.Single(o => o.InstanceIndex == 1);
        Assert.Equal((ushort)1, first.TemplateObjectId);
        Assert.Equal((ushort)2, second.TemplateObjectId);
        Assert.Equal(10f, first.PosX[^1].Value);
        Assert.Equal(20f, second.PosX[^1].Value);
    }

    [Fact]
    public void Recorder_SeedsStateFromSceneRegistry()
    {
        var registry = new Dictionary<ushort, SceneObjectTransform>
        {
            [1] = new SceneObjectTransform(1, new Vector3(5, 6, 7), Quaternion.Identity, Vector3.One),
        };
        var recorder = new SessionRecorder(registry);
        recorder.Record(new DeltaPositionEvent(1, new Vector3(1, 0, 0)));

        SessionTimeline timeline = recorder.Finalize();
        Assert.Equal(new Vector3(6, 6, 7), timeline.Samples[0].Position);
    }

    private static AnimationFileDto OptimizeSingleChannel(List<(double Time, float Value)> points, out string channelKey)
    {
        var raw = new RawObjectTimeline { ObjectId = 1 };
        raw.PosX.AddRange(points);
        channelKey = "posX";

        return AnimationOptimizer.Optimize(durationSeconds: points[^1].Time, new List<RawObjectTimeline> { raw });
    }
}
