using MoodyBlues.Backend.Compiler;

namespace MoodyBlues.Backend.Tests;

/// <summary>
/// The activity glyph the compiler stores per session. The cases are the ones the dashboard used to
/// verify when it computed this from the replay in the browser, so the move to the backend is checked
/// against the same expectations.
/// </summary>
public class SessionActivityTests
{
    private static ChannelFitter Track(params double[] times)
    {
        var fitter = new ChannelFitter(3, false, 0.001f, 1.0 / 40.0);
        fitter.KeyTimes.AddRange(times);
        return fitter;
    }

    private static CompiledReplay Replay(
        double duration,
        IReadOnlyList<ReplayObject>? objects = null,
        IReadOnlyList<ReplayEvent>? events = null,
        IReadOnlyList<ReplayLog>? logs = null) => new()
    {
        DurationSeconds = duration,
        Objects = objects ?? [new ReplayObject { ObjectId = 1, Kind = ReplayObjectKind.Scene }],
        Events = events ?? [],
        Logs = logs ?? [],
    };

    private static ReplayEvent Event(double time, ReplayEventType type, ushort objectId = 0) => new(time, type, 0, objectId, 0, 0);

    [Fact]
    public void PutsActivityWhereTheKeyframesAre()
    {
        var objects = new[] { new ReplayObject { ObjectId = 1, Position = Track(0, 1, 2, 3, 4, 5, 6, 7, 8, 9) } };

        SessionActivity activity = SessionActivity.Compute(Replay(100, objects));

        Assert.Equal(SessionActivity.BinCount, activity.Bins.Count);
        Assert.Equal(1, activity.Bins[0]);
        Assert.All(activity.Bins.Skip(10), v => Assert.Equal(0, v));
    }

    [Fact]
    public void CountsEveryChannelOfEveryObject_AndTakesTheSquareRootOfTheShare()
    {
        var objects = new[]
        {
            // Bin 0 gets 4 keys (position x2, rotation, scale), bin 24 gets 1.
            new ReplayObject { ObjectId = 1, Position = Track(0, 0.5), Rotation = Track(1), Scale = Track(60) },
            new ReplayObject { ObjectId = 2, Position = Track(1) },
        };

        SessionActivity activity = SessionActivity.Compute(Replay(100, objects));

        Assert.Equal(1, activity.Bins[0]);
        Assert.Equal(0.5, activity.Bins[28]); // 60 s of 100 s is bucket 28; sqrt(1/4)
    }

    [Fact]
    public void KeysAtOrPastTheEnd_LandInTheLastBucket()
    {
        var objects = new[] { new ReplayObject { ObjectId = 1, Position = Track(100, 120) } };

        SessionActivity activity = SessionActivity.Compute(Replay(100, objects));

        Assert.Equal(1, activity.Bins[SessionActivity.BinCount - 1]);
        Assert.Equal(0, activity.Bins[0]);
    }

    [Fact]
    public void PlacesLogsByLevelAsFractionsOfTheSession()
    {
        SessionActivity activity = SessionActivity.Compute(Replay(200, logs:
        [
            new ReplayLog(50, 3, 2, "boom"),
            new ReplayLog(100, 2, 4, "hmm"),
            new ReplayLog(150, 1, 0, "fine"),
        ]));

        Assert.Equal([0.25], activity.Errors);
        Assert.Equal([0.5], activity.Warnings);
    }

    [Fact]
    public void MarksSpawns_AndTurnsADisconnectFollowedByAReconnectIntoAGap()
    {
        SessionActivity activity = SessionActivity.Compute(Replay(100, events:
        [
            Event(20, ReplayEventType.Spawn, 2),
            Event(40, ReplayEventType.Disconnected),
            Event(60, ReplayEventType.Connected),
        ]));

        Assert.Equal([0.2], activity.Spawns);
        Assert.Equal([0.4, 0.6], Assert.Single(activity.Gaps));
    }

    [Fact]
    public void DrawsADataGapAsAThinMark_AndIgnoresTheDisconnectThatSimplyEndsTheSession()
    {
        SessionActivity activity = SessionActivity.Compute(Replay(100, events:
        [
            Event(30, ReplayEventType.DataGap),
            Event(100, ReplayEventType.Disconnected),
        ]));

        double[] gap = Assert.Single(activity.Gaps);
        Assert.Equal(0.3, gap[0], 4);
        Assert.Equal(0.006, gap[1] - gap[0], 4);
    }

    [Fact]
    public void ADisconnectWithoutAReconnect_IsNotAGap()
    {
        SessionActivity activity = SessionActivity.Compute(Replay(100, events: [Event(40, ReplayEventType.Disconnected)]));

        Assert.Empty(activity.Gaps);
    }

    [Fact]
    public void AnEmptySession_HasZeroBinsAndNoMarks()
    {
        SessionActivity activity = SessionActivity.Compute(Replay(0, logs: [new ReplayLog(0, 3, 0, "x")]));

        Assert.Equal(SessionActivity.BinCount, activity.Bins.Count);
        Assert.All(activity.Bins, v => Assert.Equal(0, v));
        Assert.Empty(activity.Errors);
        Assert.Empty(activity.Warnings);
        Assert.Empty(activity.Spawns);
        Assert.Empty(activity.Gaps);
    }

    [Fact]
    public void RoundsToFourDecimals_AndRoundTripsThroughCamelCaseJson()
    {
        SessionActivity activity = SessionActivity.Compute(Replay(3, logs: [new ReplayLog(1, 3, 0, "x")], events: [Event(2, ReplayEventType.Spawn, 2)]));

        Assert.Equal([0.3333], activity.Errors);
        Assert.Equal([0.6667], activity.Spawns);

        string json = activity.ToJson();
        Assert.StartsWith("{\"bins\":[", json);
        Assert.Contains("\"errors\":[0.3333]", json);
        Assert.Contains("\"gaps\":[]", json);

        SessionActivity? back = SessionActivity.FromJson(json);
        Assert.Equal(activity.Bins, back!.Bins);
        Assert.Equal(activity.Errors, back.Errors);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public void FromJson_WithNothingUsable_IsNull(string? json) => Assert.Null(SessionActivity.FromJson(json));
}
