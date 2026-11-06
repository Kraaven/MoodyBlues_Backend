using System.IO.Compression;
using System.Numerics;
using MoodyBlues.Backend.Compiler;
using MoodyBlues.Backend.Protocol;
using MoodyBlues.Backend.Scenes;
using MoodyBlues.Backend.Storage;

namespace MoodyBlues.Backend.Tests;

/// <summary>
/// End-to-end: wire messages, through the raw log format, through the compiler, into a replay
/// file, read back by an independent reader and compared with the motion that was "played".
/// </summary>
public class SessionCompilerTests : IDisposable
{
    private const double Tick = 1.0 / 40.0;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mb-compiler-tests-" + Guid.NewGuid().ToString("N"));

    public SessionCompilerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static SceneRegistryObject Reg(ushort id, Vector3 gltfPos, ushort parent = 0, bool invertLook = false, float radius = 0.5f, Quaternion? gltfRot = null)
    {
        Quaternion r = gltfRot ?? Quaternion.Identity;
        return new SceneRegistryObject(id, parent, [gltfPos.X, gltfPos.Y, gltfPos.Z], [r.X, r.Y, r.Z, r.W], [1, 1, 1], invertLook, false, radius);
    }

    private async Task<ReplayFileReader> CompileAsync(SceneRegistry registry, params byte[][] connections)
    {
        string rawPath = Path.Combine(_dir, "raw.mblog");
        using (var writer = new RawLogWriter(rawPath))
        {
            for (uint c = 0; c < connections.Length; c++)
            {
                writer.AppendConnectionOpened(c);
                writer.Append(RawLogRecordKind.Message, connections[c]);
                writer.AppendConnectionClosed(RawLogCloseReason.ClientClosed);
            }
        }

        string replayPath = Path.Combine(_dir, "replay.mbr.gz");
        await SessionCompiler.CompileAsync(rawPath, registry, replayPath);

        await using var file = File.OpenRead(replayPath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        await gzip.CopyToAsync(ms);
        return ReplayFileReader.Parse(ms.ToArray());
    }

    [Fact]
    public async Task ConstantVelocityInUnitySpace_ReplaysMirroredOnX_WithinTolerance()
    {
        // The object starts at Unity (2, 1, 3). UnityGLTF exported it at glTF (-2, 1, 3).
        var registry = new SceneRegistry { Objects = [Reg(1, new Vector3(-2, 1, 3))] };
        // Session time 0 is the first TimeStamp; a keepalive provides it before any motion.
        var wire = new WireBuilder().BeginSession().Time(100).KeepAlive();
        Vector3 unity = new(2, 1, 3);
        var truth = new List<(double T, Vector3 Gltf)>();
        Vector3 step = new(0.0137f, -0.0041f, 0.0083f); // deliberately not on a quantization step
        for (int i = 1; i <= 2000; i++)
        {
            double t = 100 + (i * Tick); // game time does not start at 0
            wire.Time(t).DeltaPosition(1, step);
            unity += wire.QuantizedDeltaPosition(step); // the client baselines on the decoded value
            truth.Add((t - 100, new Vector3(-unity.X, unity.Y, unity.Z)));
        }

        wire.EndSession();
        ReplayFileReader replay = await CompileAsync(registry, wire.Build());

        float tolerance = FitTolerances.Default.SurfaceDistance + (FitTolerances.Default.QuantizationAllowance * WireRanges.Default.DeltaPositionStep);
        foreach ((double t, Vector3 expected) in truth)
        {
            Vector3 got = replay.Position(1, t);
            Assert.True(Vector3.Distance(got, expected) <= tolerance * 1.01f, $"t={t}: expected {expected}, got {got}");
        }

        // Before the first event the object is at its exported pose.
        Assert.True(Vector3.Distance(replay.Position(1, 0), new Vector3(-2, 1, 3)) < 1e-4f);
        Assert.True(replay.Tracks[(1, ReplayChannel.Position)].Times.Count < 40, "constant velocity should need few keys");
        Assert.Equal(1, replay.Flags & 1); // ended cleanly
        Assert.InRange(replay.Duration, 49.9, 50.1);
    }

    [Fact]
    public async Task SpinAboutUnityY_ReplaysWithTheSameHandednessAsTheExporter()
    {
        var registry = new SceneRegistry { Objects = [Reg(1, Vector3.Zero, radius: 1f)] };
        var wire = new WireBuilder().BeginSession().Time(0).KeepAlive();
        Quaternion unity = Quaternion.Identity;
        var truth = new List<(double, Quaternion)>();
        for (int i = 1; i <= 400; i++)
        {
            double t = i * Tick;
            wire.Time(t).DeltaRotationSingleAxis(1, axis: 1, degrees: 1.5f);
            unity *= Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.5f * MathF.PI / 180f);
            truth.Add((t, CoordinateSpace.RotationUnityToGltf(unity, invertLook: false)));
        }

        ReplayFileReader replay = await CompileAsync(registry, wire.EndSession().Build());

        foreach ((double t, Quaternion expected) in truth)
        {
            float angle = ReplayFileReader.AngleBetween(replay.Rotation(1, t), expected);
            Assert.True(angle < 0.01f, $"t={t}: angle error {angle}");
        }

        // A positive Unity Y rotation is a negative glTF Y rotation (handedness flip).
        Quaternion at1s = replay.Rotation(1, 1.0);
        Assert.True(at1s.Y * at1s.W < 0, "glTF rotation should be about -Y");
    }

    [Fact]
    public void InvertLook_RoundTripsThroughSeedAndOutput()
    {
        Quaternion unity = Quaternion.Normalize(new Quaternion(0.1f, 0.4f, -0.2f, 0.9f));
        Quaternion gltf = CoordinateSpace.RotationUnityToGltf(unity, invertLook: true);
        Quaternion back = CoordinateSpace.RotationGltfToUnity(gltf, invertLook: true);
        Assert.True(ReplayFileReader.AngleBetween(unity, back) < 1e-5f);
    }

    [Fact]
    public async Task SpawnDeleteAndCascade_AreInTheTimeline()
    {
        // 10 is a prefab template; 11 is a scene object that gets reparented under the spawn.
        var registry = new SceneRegistry { Objects = [Reg(10, Vector3.Zero), Reg(11, new Vector3(5, 0, 0))] };
        byte[] msg = new WireBuilder().BeginSession()
            .Time(10.0).Instantiate(100, template: 10, parent: 0, new Vector3(1, 2, 3), Quaternion.Identity, Vector3.One)
            .Time(10.5).Reparent(11, parent: 100, new Vector3(0, 1, 0), Quaternion.Identity, Vector3.One)
            .Time(11.0).DeltaPosition(100, new Vector3(0.1f, 0, 0))
            .Time(12.0).Delete(100)
            .EndSession()
            .Build();

        ReplayFileReader replay = await CompileAsync(registry, msg);

        var spawn = replay.Events.Single(e => e.Type == ReplayEventType.Spawn);
        Assert.Equal((ushort)100, spawn.ObjectId);
        Assert.Equal((ushort)10, spawn.A);
        Assert.Equal(0.0, spawn.Time, 6);

        var reparent = replay.Events.Single(e => e.Type == ReplayEventType.Reparent);
        Assert.Equal((ushort)11, reparent.ObjectId);
        Assert.Equal((ushort)100, reparent.A);

        // One Delete event for the root; the reparented child is deleted with it (Spec 10.1).
        Assert.Single(replay.Events, e => e.Type == ReplayEventType.Delete);
        Assert.Equal(2.0f, replay.Objects.Single(o => o.Id == 100).DeleteTime, 4);
        Assert.Equal(2.0f, replay.Objects.Single(o => o.Id == 11).DeleteTime, 4);

        // Spawned at Unity (1,2,3) -> glTF (-1,2,3); the child jumps at the reparent instant.
        Assert.True(Vector3.Distance(replay.Position(100, 0.0), new Vector3(-1, 2, 3)) < 0.02f);
        Assert.True(Vector3.Distance(replay.Position(11, 0.49), new Vector3(5, 0, 0)) < 1e-3f);
        Assert.True(Vector3.Distance(replay.Position(11, 0.5), new Vector3(0, 1, 0)) < 0.02f);
        Assert.Equal(ReplayObjectKind.Spawned, replay.Objects.Single(o => o.Id == 100).Kind);
    }

    [Fact]
    public async Task MissingEndSession_IsFlaggedAsTruncated_AndReconnectsAreMarked()
    {
        var registry = new SceneRegistry { Objects = [Reg(1, Vector3.Zero)] };
        byte[] first = new WireBuilder().BeginSession().Time(1).DeltaPosition(1, new Vector3(0.1f, 0, 0)).Build();
        byte[] second = new WireBuilder().BeginSession().Time(3).DeltaPosition(1, new Vector3(0.1f, 0, 0)).Build();

        ReplayFileReader replay = await CompileAsync(registry, first, second);

        Assert.Equal(0, replay.Flags & 1);
        Assert.Equal(255, replay.EndReason);
        Assert.Equal(2, replay.Events.Count(e => e.Type == ReplayEventType.Connected));
        Assert.Equal(2, replay.ConnectionCount);
        Assert.True(Vector3.Distance(replay.Position(1, 2.0), new Vector3(-0.2f, 0, 0)) < 0.01f);
    }

    [Fact]
    public async Task LongSession_SplitsIntoBlocks_ThatConcatenateSeamlessly()
    {
        var registry = new SceneRegistry { Objects = [Reg(1, Vector3.Zero), Reg(2, Vector3.Zero)] };
        var wire = new WireBuilder().BeginSession().Time(0).KeepAlive();
        Vector3 p = Vector3.Zero;
        for (int i = 1; i <= 40 * 150; i++)
        {
            double t = i * Tick;
            // Object 1 moves only in the first 30 s; object 2 keeps moving along a curve.
            if (t < 30) wire.Time(t).DeltaPosition(1, new Vector3(0.01f, 0, 0));
            Vector3 next = new((float)Math.Sin(t * 0.3) * 4, 0, (float)Math.Cos(t * 0.3) * 4);
            wire.Time(t).TruePosition(2, next);
            p = next;
        }

        ReplayFileReader replay = await CompileAsync(registry, wire.EndSession().Build());

        Assert.Equal(3, replay.BlockCount);
        // Object 1 stopped moving in block 0 but must keep its final pose in later blocks.
        Assert.True(Vector3.Distance(replay.Position(1, 140), replay.Position(1, 31)) < 1e-5f);
        // Object 2's curve evaluates continuously across the block boundaries.
        foreach (double t in new[] { 59.99, 60.0, 60.01, 119.99, 120.0, 120.01 })
        {
            var expected = new Vector3(-(float)Math.Sin(t * 0.3) * 4, 0, (float)Math.Cos(t * 0.3) * 4);
            Assert.True(Vector3.Distance(replay.Position(2, t), expected) < 0.03f, $"t={t}");
        }
    }

    [Fact]
    public async Task TransformsForUnknownObjects_AreIgnoredAndFlagged()
    {
        var registry = new SceneRegistry { Objects = [Reg(1, Vector3.Zero)] };
        byte[] msg = new WireBuilder().BeginSession().Time(1).DeltaPosition(999, new Vector3(0.1f, 0, 0)).EndSession().Build();

        ReplayFileReader replay = await CompileAsync(registry, msg);

        Assert.DoesNotContain(replay.Objects, o => o.Id == 999);
        Assert.Equal(4, replay.Flags & 4);
    }
}
