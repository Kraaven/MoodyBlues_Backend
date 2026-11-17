using System.Buffers.Binary;
using System.Numerics;
using MoodyBlues.Backend.Compiler;

namespace MoodyBlues.Backend.Tests;

/// <summary>
/// Test-side reader and evaluator for the replay file format, written independently from
/// <see cref="ReplayFileWriter"/> against its documented layout (format 3). It decodes varints with
/// arithmetic, like a JavaScript reader has to, and evaluates Hermite curves the way three.js's
/// BezierInterpolant does with control points at one third.
/// </summary>
public sealed class ReplayFileReader
{
    public sealed record Obj(ushort Id, ReplayObjectKind Kind, byte Mask, ushort Template, ushort Parent, double SpawnTime, float DeleteTime);

    public sealed record Evt(double Time, ReplayEventType Type, byte Aux, ushort ObjectId, ushort A, ushort B);

    public sealed class Track
    {
        public required int Dim { get; init; }
        public List<double> Times { get; } = [];
        public List<float> Values { get; } = [];
        public List<float> In { get; } = [];
        public List<float> Out { get; } = [];
    }

    public ushort Flags { get; private set; }
    public double Duration { get; private set; }
    public int TrackCount { get; private set; }
    public uint TotalKeys { get; private set; }
    public uint HardClamps { get; private set; }
    public byte EndReason { get; private set; }
    public ushort ConnectionCount { get; private set; }
    public List<Obj> Objects { get; } = [];
    public List<Evt> Events { get; } = [];
    public Dictionary<(ushort Id, ReplayChannel Channel), Track> Tracks { get; } = [];

    public static ReplayFileReader Parse(byte[] data)
    {
        var r = new ReplayFileReader();
        ReadOnlySpan<byte> d = data;
        if (!d[..4].SequenceEqual("MBRP"u8))
        {
            throw new InvalidDataException("bad magic");
        }

        Assert.Equal(ReplayFileWriter.FormatVersion, BinaryPrimitives.ReadUInt16LittleEndian(d[4..]));
        r.Flags = BinaryPrimitives.ReadUInt16LittleEndian(d[6..]);
        r.Duration = BinaryPrimitives.ReadDoubleLittleEndian(d[8..]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(d[16..]));
        int objectCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[20..]);
        int eventCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[24..]);
        r.TrackCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[28..]);
        int objectOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[32..]);
        int eventOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[36..]);
        int trackDataOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[40..]);
        r.TotalKeys = BinaryPrimitives.ReadUInt32LittleEndian(d[44..]);
        r.HardClamps = BinaryPrimitives.ReadUInt32LittleEndian(d[52..]);
        r.EndReason = d[60];
        r.ConnectionCount = BinaryPrimitives.ReadUInt16LittleEndian(d[62..]);

        for (int i = 0; i < objectCount; i++)
        {
            ReadOnlySpan<byte> e = d.Slice(objectOffset + (i * 20), 20);
            r.Objects.Add(new Obj(
                BinaryPrimitives.ReadUInt16LittleEndian(e),
                (ReplayObjectKind)e[2],
                e[3],
                BinaryPrimitives.ReadUInt16LittleEndian(e[4..]),
                BinaryPrimitives.ReadUInt16LittleEndian(e[6..]),
                BinaryPrimitives.ReadDoubleLittleEndian(e[8..]),
                BinaryPrimitives.ReadSingleLittleEndian(e[16..])));
        }

        for (int i = 0; i < eventCount; i++)
        {
            ReadOnlySpan<byte> e = d.Slice(eventOffset + (i * 16), 16);
            r.Events.Add(new Evt(
                BinaryPrimitives.ReadDoubleLittleEndian(e),
                (ReplayEventType)e[8],
                e[9],
                BinaryPrimitives.ReadUInt16LittleEndian(e[10..]),
                BinaryPrimitives.ReadUInt16LittleEndian(e[12..]),
                BinaryPrimitives.ReadUInt16LittleEndian(e[14..])));
        }

        Assert.Equal(0, trackDataOffset % 4);
        int p = trackDataOffset;
        (ushort Id, ReplayChannel Channel)? previous = null;
        for (int t = 0; t < r.TrackCount; t++)
        {
            ushort id = (ushort)ReadVarint(d, ref p);
            var channel = (ReplayChannel)d[p++];
            if (previous is { } prev)
            {
                Assert.True((id, channel).CompareTo(prev) > 0, "tracks must ascend by (objectId, channel)");
            }

            previous = (id, channel);
            int keys = (int)ReadVarint(d, ref p);
            Assert.True(keys >= 1);
            int dim = channel == ReplayChannel.Rotation ? 4 : 3;
            bool rotation = channel == ReplayChannel.Rotation;
            double valueGrid = rotation ? ReplayFileWriter.RotationGrid : ReplayFileWriter.PositionScaleGrid;
            var track = new Track { Dim = dim };

            long acc = 0;
            for (int k = 0; k < keys; k++)
            {
                acc += ReadZigzag(d, ref p);
                track.Times.Add(acc / ReplayFileWriter.TimeGrid);
            }

            var values = new float[keys * dim];
            for (int c = 0; c < dim; c++)
            {
                acc = 0;
                for (int k = 0; k < keys; k++)
                {
                    acc += ReadZigzag(d, ref p);
                    values[(k * dim) + c] = (float)(acc / valueGrid);
                }
            }

            // Tangents are stored as m * dt on the value grid; the first in and last out are absent (0).
            var inTan = new float[keys * dim];
            var outTan = new float[keys * dim];
            for (int c = 0; c < dim; c++)
            {
                for (int k = 0; k < keys; k++)
                {
                    if (k > 0)
                    {
                        double dt = track.Times[k] - track.Times[k - 1];
                        if (dt <= 0) throw new InvalidDataException("non-increasing key times");
                        inTan[(k * dim) + c] = (float)(ReadZigzag(d, ref p) / valueGrid / dt);
                    }

                    if (k < keys - 1)
                    {
                        double dt = track.Times[k + 1] - track.Times[k];
                        if (dt <= 0) throw new InvalidDataException("non-increasing key times");
                        outTan[(k * dim) + c] = (float)(ReadZigzag(d, ref p) / valueGrid / dt);
                    }
                }
            }

            track.Values.AddRange(values);
            track.In.AddRange(inTan);
            track.Out.AddRange(outTan);
            r.Tracks[(id, channel)] = track;
        }

        Assert.Equal(d.Length, p); // track data runs exactly to the end of the file
        return r;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> d, ref int p)
    {
        ulong result = 0;
        double scale = 1; // 128^i, arithmetic only
        for (int i = 0; i < 8; i++)
        {
            byte b = d[p++];
            result += (ulong)(b & 0x7F) * (ulong)scale;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            scale *= 128;
        }

        throw new InvalidDataException("varint longer than 8 bytes");
    }

    private static long ReadZigzag(ReadOnlySpan<byte> d, ref int p)
    {
        ulong u = ReadVarint(d, ref p);
        return (long)(u >> 1) ^ -(long)(u & 1);
    }

    /// <summary>Evaluates a track at time t: clamped before the first and after the last key.</summary>
    public float[] Evaluate(ushort id, ReplayChannel channel, double t)
    {
        Track track = Tracks[(id, channel)];
        int n = track.Times.Count;
        int dim = track.Dim;
        var result = new float[dim];
        if (t <= track.Times[0] || n == 1)
        {
            for (int c = 0; c < dim; c++) result[c] = track.Values[c];
            return Normalize(result, channel);
        }

        if (t >= track.Times[n - 1])
        {
            for (int c = 0; c < dim; c++) result[c] = track.Values[((n - 1) * dim) + c];
            return Normalize(result, channel);
        }

        int i = track.Times.BinarySearch(t);
        if (i < 0) i = ~i;
        int k1 = i;
        int k0 = i - 1;
        double t0 = track.Times[k0];
        double dt = track.Times[k1] - t0;
        double u = (t - t0) / dt;
        double u2 = u * u, u3 = u2 * u;
        double h00 = (2 * u3) - (3 * u2) + 1, h10 = u3 - (2 * u2) + u, h01 = (-2 * u3) + (3 * u2), h11 = u3 - u2;
        for (int c = 0; c < dim; c++)
        {
            double v0 = track.Values[(k0 * dim) + c];
            double v1 = track.Values[(k1 * dim) + c];
            double m0 = track.Out[(k0 * dim) + c];
            double m1 = track.In[(k1 * dim) + c];
            result[c] = (float)((h00 * v0) + (h10 * dt * m0) + (h01 * v1) + (h11 * dt * m1));
        }

        return Normalize(result, channel);
    }

    public Vector3 Position(ushort id, double t)
    {
        float[] v = Evaluate(id, ReplayChannel.Position, t);
        return new Vector3(v[0], v[1], v[2]);
    }

    public Quaternion Rotation(ushort id, double t)
    {
        float[] v = Evaluate(id, ReplayChannel.Rotation, t);
        return new Quaternion(v[0], v[1], v[2], v[3]);
    }

    private static float[] Normalize(float[] v, ReplayChannel channel)
    {
        if (channel != ReplayChannel.Rotation) return v;
        float len = MathF.Sqrt((v[0] * v[0]) + (v[1] * v[1]) + (v[2] * v[2]) + (v[3] * v[3]));
        for (int c = 0; c < 4; c++) v[c] /= len;
        return v;
    }

    public static float AngleBetween(Quaternion a, Quaternion b) =>
        2f * MathF.Acos(MathF.Min(1f, MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b)))));
}
