using System.IO.Compression;
using MoodyBlues.Backend.Compiler;
using MoodyBlues.Backend.Protocol;

namespace MoodyBlues.Backend.Tests;

/// <summary>Replay format 3: one varint track section, quantized times, values and tangents.</summary>
public class ReplayFormatTests
{
    private const double Tick = 1.0 / 40.0;

    private static ChannelFitter Fitter(int dim, bool rotation, int keys, Func<double, float[]> sample)
    {
        var f = new ChannelFitter(dim, rotation, 1e-5f, Tick);
        for (int i = 0; i < keys; i++)
        {
            double t = i * Tick;
            f.Add(t, sample(t));
        }

        f.Flush();
        return f;
    }

    private static CompiledReplay Replay()
    {
        var spinner = new ReplayObject { ObjectId = 7 };
        spinner.Position = Fitter(3, false, 200, t => [(float)(3 * t), (float)(8 * t - 4.905 * t * t), -2.5f]);
        spinner.Rotation = Fitter(4, true, 200, t =>
        {
            var q = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, (float)(t * 1.3));
            return [q.X, q.Y, q.Z, q.W];
        });
        spinner.Scale = Fitter(3, false, 60, t => [1f + (float)(0.1 * t), 1f, 1f]);

        var early = new ReplayObject { ObjectId = 2 };
        early.Position = Fitter(3, false, 1, _ => [-4.25f, 0.5f, 1234.5f]);

        var spawned = new ReplayObject { ObjectId = 300, Kind = ReplayObjectKind.Spawned, TemplateObjectId = 2, InitialParentObjectId = 0, SpawnTime = 1.5, DeleteTime = 4.0 };

        return new CompiledReplay
        {
            DurationSeconds = 5.0,
            Objects = [spinner, spawned, early], // deliberately unsorted: tracks must still ascend
            Events =
            [
                new ReplayEvent(2.0, ReplayEventType.Hide, 0, 2, 0, 0),
                new ReplayEvent(1.5, ReplayEventType.Spawn, 0, 300, 2, 0),
            ],
            Logs = [new ReplayLog(0.5, 2, 9, "warning é中"), new ReplayLog(0.5, 1, 3, "second")],
            EndedCleanly = true,
            EndReason = SessionEndReason.ApplicationQuit,
            ConnectionCount = 2,
            DeltaPromotions = 4,
            HardClamps = 1,
            DroppedEvents = 6,
        };
    }

    [Fact]
    public void RoundTrip_TimesAndValuesStayOnTheirQuantizationGrids_AndTablesAreUnchanged()
    {
        CompiledReplay replay = Replay();

        ReplayFileReader r = ReplayFileReader.Parse(ReplayFileWriter.Serialize(replay));

        Assert.Equal(3, ReplayFileWriter.FormatVersion);
        Assert.Equal(5.0, r.Duration);
        Assert.Equal(4, r.TrackCount);
        Assert.Equal((uint)replay.KeyframeCount, r.TotalKeys);
        Assert.Equal(1u, r.HardClamps);
        Assert.Equal(2, r.ConnectionCount);
        Assert.Equal(3, r.Objects.Count);
        Assert.Equal(ReplayObjectKind.Spawned, r.Objects[1].Kind);
        Assert.Equal(1.5, r.Objects[1].SpawnTime);
        Assert.Equal(4.0f, r.Objects[1].DeleteTime);
        Assert.Equal([ReplayEventType.Spawn, ReplayEventType.Hide], r.Events.Select(e => e.Type));
        Assert.Equal(1.5, r.Events[0].Time);

        foreach (ReplayObject o in replay.Objects)
        {
            foreach (ReplayChannel channel in new[] { ReplayChannel.Position, ReplayChannel.Rotation, ReplayChannel.Scale })
            {
                ChannelFitter? f = o.Channel(channel);
                if (f is null)
                {
                    Assert.False(r.Tracks.ContainsKey((o.ObjectId, channel)));
                    continue;
                }

                ReplayFileReader.Track track = r.Tracks[(o.ObjectId, channel)];
                bool rot = channel == ReplayChannel.Rotation;
                double valueTol = (0.5 / (rot ? ReplayFileWriter.RotationGrid : ReplayFileWriter.PositionScaleGrid)) + 1e-6;
                // Tangents are stored as m * dt on the value grid, so m * dt is within half a value step.
                double segmentTol = valueTol;
                Assert.Equal(f.KeyCount, track.Times.Count);
                for (int k = 0; k < f.KeyCount; k++)
                {
                    Assert.InRange(Math.Abs(track.Times[k] - f.KeyTimes[k]), 0, (0.5 / ReplayFileWriter.TimeGrid) + 1e-9);
                    for (int c = 0; c < f.Dimension; c++)
                    {
                        int i = (k * f.Dimension) + c;
                        Assert.InRange(Math.Abs(track.Values[i] - f.KeyValues[i]), 0, valueTol);
                        if (k > 0)
                        {
                            double dt = track.Times[k] - track.Times[k - 1];
                            Assert.InRange(Math.Abs((track.In[i] - f.KeyInTangents[i]) * dt), 0, segmentTol + 1e-6);
                        }
                        else
                        {
                            Assert.Equal(0f, track.In[i]);
                        }

                        if (k < f.KeyCount - 1)
                        {
                            double dt = track.Times[k + 1] - track.Times[k];
                            Assert.InRange(Math.Abs((track.Out[i] - f.KeyOutTangents[i]) * dt), 0, segmentTol + 1e-6);
                        }
                        else
                        {
                            Assert.Equal(0f, track.Out[i]);
                        }
                    }
                }
            }
        }

        // A large coordinate still decodes: 1234.5 needs a multi-byte varint.
        Assert.Equal(1234.5f, r.Tracks[(2, ReplayChannel.Position)].Values[2], 3);
    }

    [Fact]
    public void Logs_AreWrittenInTimeOrderWithTheirText()
    {
        byte[] bytes = ReplayFileWriter.Serialize(Replay());

        var logCount = BitConverter.ToUInt32(bytes, 64);
        var logOffset = BitConverter.ToUInt32(bytes, 68);
        var stringOffset = BitConverter.ToUInt32(bytes, 72);
        var stringLength = BitConverter.ToUInt32(bytes, 76);
        Assert.Equal(2u, logCount);
        Assert.Equal(0u, stringOffset % 4);
        string text = System.Text.Encoding.UTF8.GetString(bytes, (int)stringOffset, (int)stringLength);
        Assert.Equal("warning é中second", text);
        Assert.Equal(2, bytes[(int)logOffset + 8]); // first log is the warning, same-time order kept
        Assert.Equal(1, bytes[(int)logOffset + 20 + 8]);
    }

    [Fact]
    public async Task Brotli_RoundTripsAndReportsTheDecodedLength()
    {
        CompiledReplay replay = Replay();
        using var ms = new MemoryStream();

        long decoded = await ReplayFileWriter.WriteBrotliAsync(replay, ms);

        ms.Position = 0;
        using var brotli = new BrotliStream(ms, CompressionMode.Decompress);
        using var plain = new MemoryStream();
        await brotli.CopyToAsync(plain);
        Assert.Equal(decoded, plain.Length);
        Assert.Equal(ReplayFileWriter.Serialize(replay), plain.ToArray());
    }

    [Fact]
    public void ExtremeValues_AreClampedToTheSafeIntegerRange()
    {
        var f = new ChannelFitter(3, false, 1e-5f, Tick);
        f.Add(0, [1e30f, -1e30f, 0f]);
        f.Flush();
        var obj = new ReplayObject { ObjectId = 1, Position = f };

        ReplayFileReader r = ReplayFileReader.Parse(ReplayFileWriter.Serialize(new CompiledReplay { Objects = [obj], DurationSeconds = 0 }));

        double limit = ReplayFileWriter.QuantizedLimit / ReplayFileWriter.PositionScaleGrid;
        Assert.Equal((float)limit, r.Tracks[(1, ReplayChannel.Position)].Values[0]);
        Assert.Equal((float)-limit, r.Tracks[(1, ReplayChannel.Position)].Values[1]);
    }

    [Fact]
    public void FittingToleranceDefaults_FollowTheWorstCaseWireError()
    {
        WireRanges r = WireRanges.Default;

        Assert.Equal(1f, FitTolerances.Default.QuantizationAllowance);
        // Half a step on each axis (X, Z: 1.5/1023; Y: 1.5/511), as a vector length.
        Assert.Equal(0.5f * MathF.Sqrt((2f * MathF.Pow(1.5f / 1023f, 2)) + MathF.Pow(1.5f / 511f, 2)), r.DeltaPositionError, 7);
        Assert.Equal(0.0017971f, r.DeltaPositionError, 6);
        // sqrt(3) * (0.25 / 127): half a step on each of three components, doubled into an angle.
        Assert.Equal(0.0034096f, r.DeltaRotationAngleError, 6);
        // sqrt(3) / 2 * (1 / 127).
        Assert.Equal(0.0068190f, r.DeltaScaleError, 6);
    }
}
