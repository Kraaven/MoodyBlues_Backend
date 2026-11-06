using System.Numerics;
using MoodyBlues.Backend.Compiler;

namespace MoodyBlues.Backend.Tests;

public class ChannelFitterTests
{
    private const double Tick = 1.0 / 40.0;

    private static float[] EvalVec(ChannelFitter f, double t)
    {
        var track = ToTrack(f);
        return track.Evaluate(1, ReplayChannel.Position, t);
    }

    /// <summary>Wraps a fitter's keys in a replay so the independent test reader evaluates them.</summary>
    private static ReplayFileReader ToTrack(ChannelFitter f, ReplayChannel channel = ReplayChannel.Position)
    {
        var obj = new ReplayObject { ObjectId = 1 };
        if (channel == ReplayChannel.Position) obj.Position = f;
        else if (channel == ReplayChannel.Rotation) obj.Rotation = f;
        else obj.Scale = f;
        var replay = new CompiledReplay { Objects = [obj], DurationSeconds = f.KeyTimes[^1] };
        return ReplayFileReader.Parse(ReplayFileWriter.Serialize(replay, blockDuration: 7.0));
    }

    private static void AssertWithinTolerance(ChannelFitter f, IReadOnlyList<(double T, Vector3 V)> samples, float tolerance)
    {
        ReplayFileReader reader = ToTrack(f);
        foreach ((double t, Vector3 v) in samples)
        {
            Vector3 got = reader.Position(1, t);
            Assert.True(Vector3.Distance(got, v) <= tolerance * 1.001f + 1e-6f, $"t={t}: expected {v}, got {got}");
        }
    }

    [Fact]
    public void ConstantValue_IsOneKey()
    {
        var f = new ChannelFitter(3, false, 0.001f, Tick);
        f.Add(0, [1, 2, 3]);
        f.Flush();

        Assert.Equal(1, f.KeyCount);
    }

    [Fact]
    public void ConstantVelocity_CollapsesToAFewKeys()
    {
        var f = new ChannelFitter(3, false, 0.001f, Tick);
        var samples = new List<(double, Vector3)>();
        for (int i = 0; i <= 4000; i++)
        {
            double t = i * Tick;
            var v = new Vector3((float)(0.5 * t), 1f, (float)(-0.25 * t));
            samples.Add((t, v));
            f.Add(t, [v.X, v.Y, v.Z]);
        }

        f.Flush();

        Assert.True(f.KeyCount <= 20, $"expected a handful of keys for a straight line, got {f.KeyCount}");
        AssertWithinTolerance(f, samples, 0.001f);
    }

    [Fact]
    public void Parabola_IsReproducedWithFarFewerKeysThanSamples()
    {
        // A projectile under gravity: exactly quadratic, which a cubic Hermite represents exactly.
        var f = new ChannelFitter(3, false, 0.001f, Tick);
        var samples = new List<(double, Vector3)>();
        for (int i = 0; i <= 200; i++)
        {
            double t = i * Tick;
            var v = new Vector3((float)(3 * t), (float)((8 * t) - (4.905 * t * t)), 0f);
            samples.Add((t, v));
            f.Add(t, [v.X, v.Y, v.Z]);
        }

        f.Flush();

        Assert.True(f.KeyCount <= 6, $"a parabola should need very few keys, got {f.KeyCount}");
        AssertWithinTolerance(f, samples, 0.001f);
    }

    [Fact]
    public void IrregularMotion_StaysWithinTolerance()
    {
        var rng = new Random(1234);
        var f = new ChannelFitter(3, false, 0.002f, Tick);
        var samples = new List<(double, Vector3)>();
        Vector3 p = Vector3.Zero, vel = Vector3.Zero;
        for (int i = 0; i <= 3000; i++)
        {
            double t = i * Tick;
            vel += new Vector3((float)(rng.NextDouble() - 0.5), (float)(rng.NextDouble() - 0.5), (float)(rng.NextDouble() - 0.5)) * 0.2f;
            p += vel * (float)Tick;
            samples.Add((t, p));
            f.Add(t, [p.X, p.Y, p.Z]);
        }

        f.Flush();

        Assert.True(f.KeyCount < samples.Count / 3, $"expected meaningful reduction, got {f.KeyCount} keys for {samples.Count} samples");
        AssertWithinTolerance(f, samples, 0.002f);
    }

    [Fact]
    public void StillPeriod_GetsAHoldKey_SoTheCurveDoesNotDriftAcrossIt()
    {
        var f = new ChannelFitter(3, false, 0.001f, Tick);
        f.Add(0, [0, 0, 0]);
        // Nothing is sent while still. Motion resumes at t=5.
        f.Add(5.0, [0.1f, 0, 0]);
        f.Add(5.0 + Tick, [0.2f, 0, 0]);
        f.Flush();

        float[] mid = EvalVec(f, 2.5);
        Assert.Equal(0f, mid[0], 5);
        float[] justBefore = EvalVec(f, 5.0 - Tick);
        Assert.Equal(0f, justBefore[0], 5);
    }

    [Fact]
    public void Discontinuity_JumpsWithinAMillisecond()
    {
        var f = new ChannelFitter(3, false, 0.001f, Tick);
        f.Add(0, [0, 0, 0]);
        f.Add(1.0, [10, 0, 0], discontinuity: true);
        f.Flush();

        Assert.Equal(0f, EvalVec(f, 1.0 - (2 * ChannelFitter.StepEpsilon))[0], 4);
        Assert.Equal(10f, EvalVec(f, 1.0)[0], 4);
    }

    [Fact]
    public void Rotation_SpinIsWithinAngleTolerance_AndSurvivesSignFlips()
    {
        const float tol = 0.001f; // radians
        var f = new ChannelFitter(4, true, tol, Tick);
        var truth = new List<(double T, Quaternion Q)>();
        for (int i = 0; i <= 800; i++)
        {
            double t = i * Tick;
            // 180 deg/s about a tilted axis; feed every other sample with flipped sign, which is
            // the same rotation and is what canonicalised wire decoding can produce.
            Quaternion q = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(0.2f, 1f, 0.1f)), (float)(Math.PI * t));
            truth.Add((t, q));
            Quaternion fed = i % 2 == 0 ? q : -q;
            f.Add(t, [fed.X, fed.Y, fed.Z, fed.W]);
        }

        f.Flush();

        ReplayFileReader reader = ToTrack(f, ReplayChannel.Rotation);
        foreach ((double t, Quaternion q) in truth)
        {
            float angle = ReplayFileReader.AngleBetween(reader.Rotation(1, t), q);
            Assert.True(angle <= tol * 1.01f, $"t={t}: angle error {angle}");
        }

        Assert.True(f.KeyCount < truth.Count / 4, $"got {f.KeyCount} keys");
    }

    [Fact]
    public void SameTimeSamples_StayStrictlyIncreasing()
    {
        var f = new ChannelFitter(3, false, 0.001f, Tick);
        f.Add(1.0, [0, 0, 0]);
        f.Add(1.0, [1, 0, 0]);
        f.Flush();

        for (int i = 1; i < f.KeyCount; i++)
        {
            Assert.True(f.KeyTimes[i] > f.KeyTimes[i - 1]);
        }
    }
}
