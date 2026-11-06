using System.Numerics;
using MoodyBlues.Backend.Protocol;

namespace MoodyBlues.Backend.Tests;

public class WireDecoderTests
{
    private sealed class RecordingSink : IWireEventSink
    {
        public List<string> Log { get; } = [];
        public List<(ushort Id, TransformUpdate U)> Transforms { get; } = [];
        public BeginSessionInfo? Begin { get; private set; }
        public EndSessionInfo? End { get; private set; }
        public (ushort Id, ushort Template, ushort Parent, Vector3 P, Quaternion R, Vector3 S)? Spawn { get; private set; }
        public (ushort Id, ushort Parent, Vector3 P)? Reparent { get; private set; }

        public void OnTimeStamp(double seconds) => Log.Add($"t={seconds}");
        public void OnBeginSession(in BeginSessionInfo info) { Begin = info; Log.Add("begin"); }
        public void OnKeepAlive() => Log.Add("keepalive");
        public void OnEndSession(in EndSessionInfo info) { End = info; Log.Add("end"); }
        public void OnShow(ushort objectId) => Log.Add($"show {objectId}");
        public void OnHide(ushort objectId) => Log.Add($"hide {objectId}");
        public void OnDelete(ushort objectId) => Log.Add($"delete {objectId}");
        public void OnInstantiate(ushort objectId, ushort templateObjectId, ushort parentObjectId, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            Spawn = (objectId, templateObjectId, parentObjectId, position, rotation, scale);
            Log.Add($"spawn {objectId}");
        }

        public void OnReparent(ushort objectId, ushort newParentObjectId, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            Reparent = (objectId, newParentObjectId, position);
            Log.Add($"reparent {objectId}");
        }

        public void OnTransform(ushort objectId, in TransformUpdate update)
        {
            Transforms.Add((objectId, update));
            Log.Add($"transform {objectId}");
        }

        public void OnLog(byte style, ReadOnlySpan<byte> utf8Text) => Log.Add($"log {style}");
    }

    [Fact]
    public void BeginSession_IsDecoded_AndItsRangesApplyToLaterEvents()
    {
        var ranges = WireRanges.Default with { PositionRange = 50f };
        byte[] msg = new WireBuilder { Ranges = ranges }.BeginSession(captureHz: 30f).Time(1.0).TruePosition(5, new Vector3(12.3f, -4f, 0.5f)).Build();
        var sink = new RecordingSink();
        var decoder = new WireDecoder();

        decoder.DecodeMessage(msg, sink);

        Assert.Equal(30f, sink.Begin!.Value.CaptureHz);
        Assert.Equal(50f, decoder.Ranges.PositionRange);
        Vector3 p = sink.Transforms.Single().U.Position;
        // With +/-50 over 16 bits the step is 0.0015; the default +/-500 would be ten times coarser.
        Assert.InRange(MathF.Abs(p.X - 12.3f), 0f, 0.001f);
    }

    [Fact]
    public void FirstEventMustBeBeginSession()
    {
        byte[] msg = new WireBuilder().Time(1.0).Show(3).Build();
        var ex = Assert.Throws<ProtocolException>(() => new WireDecoder().DecodeMessage(msg, new RecordingSink()));
        Assert.Contains("BeginSession", ex.Message);
    }

    [Fact]
    public void UnsupportedProtocolVersion_IsRejected()
    {
        byte[] msg = new WireBuilder().BeginSession(version: ProtocolConstants.ProtocolVersion + 1).Build();
        var ex = Assert.Throws<ProtocolException>(() => new WireDecoder().DecodeMessage(msg, new RecordingSink()));
        Assert.Contains("version", ex.Message);
    }

    [Fact]
    public void MalformedMessage_EmitsNothing()
    {
        // A valid prefix followed by an unknown type byte: the whole message is refused, so the
        // sink never sees the events before the bad byte.
        byte[] msg = new WireBuilder().BeginSession().Time(1.0).Show(3).Raw(99).Build();
        var sink = new RecordingSink();

        Assert.Throws<ProtocolException>(() => new WireDecoder().DecodeMessage(msg, sink));
        Assert.Empty(sink.Log);
    }

    [Fact]
    public void TruncatedEvent_IsRejected()
    {
        byte[] full = new WireBuilder().BeginSession().TruePosition(1, Vector3.One).Build();
        Assert.Throws<ProtocolException>(() => new WireDecoder().DecodeMessage(full[..^2], new RecordingSink()));
    }

    [Fact]
    public void LifecycleAndMaintenanceEvents_AreDecoded()
    {
        byte[] msg = new WireBuilder()
            .BeginSession()
            .Time(2.5).Show(7).Hide(8).Delete(9).KeepAlive()
            .Instantiate(20, template: 3, parent: 4, new Vector3(1, 2, 3), Quaternion.Identity, Vector3.One)
            .Reparent(20, parent: 5, new Vector3(-1, 0, 2), Quaternion.Identity, new Vector3(2, 2, 2))
            .EndSession(SessionEndReason.SceneUnloaded, promotions: 4, widenings: 3, hardClamps: 2, dropped: 1)
            .Build();
        var sink = new RecordingSink();

        new WireDecoder().DecodeMessage(msg, sink);

        Assert.Equal(["begin", "t=2.5", "show 7", "hide 8", "delete 9", "keepalive", "spawn 20", "reparent 20", "end"], sink.Log);
        Assert.Equal((ushort)3, sink.Spawn!.Value.Template);
        Assert.Equal((ushort)4, sink.Spawn.Value.Parent);
        Assert.InRange(Vector3.Distance(sink.Spawn.Value.P, new Vector3(1, 2, 3)), 0f, 0.02f);
        Assert.Equal((ushort)5, sink.Reparent!.Value.Parent);
        Assert.Equal(SessionEndReason.SceneUnloaded, sink.End!.Value.Reason);
        Assert.Equal(2u, sink.End.Value.HardClamps);
        Assert.Equal(1u, sink.End.Value.DroppedEvents);
    }

    [Fact]
    public void DeltaEvents_AreMarkedAsDeltas()
    {
        byte[] msg = new WireBuilder().BeginSession().Time(1).DeltaPosition(1, new Vector3(0.05f, -0.02f, 0.1f)).DeltaRotationSingleAxis(2, axis: 1, degrees: 1f).Build();
        var sink = new RecordingSink();

        new WireDecoder().DecodeMessage(msg, sink);

        TransformUpdate pos = sink.Transforms[0].U;
        Assert.True(pos.Has(TransformFields.Position | TransformFields.PositionIsDelta));
        Assert.False(pos.Has(TransformFields.Rotation));
        Assert.InRange(Vector3.Distance(pos.Position, new Vector3(0.05f, -0.02f, 0.1f)), 0f, 0.002f);

        TransformUpdate rot = sink.Transforms[1].U;
        Assert.True(rot.Has(TransformFields.RotationIsDelta));
        Quaternion expected = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 180f);
        Assert.InRange(ReplayFileReader.AngleBetween(rot.Rotation, expected), 0f, 1e-4f);
    }

    [Fact]
    public void SmallestThree_RoundTripsArbitraryRotation()
    {
        Quaternion q = Quaternion.Normalize(new Quaternion(0.3f, -0.5f, 0.2f, 0.78f));
        byte[] msg = new WireBuilder().BeginSession().TrueRotation(1, q).Build();
        var sink = new RecordingSink();

        new WireDecoder().DecodeMessage(msg, sink);

        Assert.InRange(ReplayFileReader.AngleBetween(sink.Transforms[0].U.Rotation, q), 0f, 0.005f);
    }

    [Fact]
    public void SingleAxisWithInvalidAxis_IsRejectedBeforeAnyEventIsEmitted()
    {
        // Axis id 3 does not exist; set the low two bits of the payload to 3.
        byte[] msg = new WireBuilder().BeginSession().Raw(EncodingHelpers.Envelope(EventType.TrueRotationSingleAxis, 1)).Raw(0x03, 0x00).Build();
        var sink = new RecordingSink();

        Assert.Throws<ProtocolException>(() => new WireDecoder().DecodeMessage(msg, sink));
        Assert.Empty(sink.Log);
    }
}
