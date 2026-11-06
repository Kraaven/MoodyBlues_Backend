using System.Buffers.Binary;
using System.Numerics;
using MoodyBlues.Backend.Compiler;

namespace MoodyBlues.Backend.Tests;

/// <summary>
/// Test-side reader and evaluator for the replay file format, written independently from
/// <see cref="ReplayFileWriter"/> against its documented layout. It concatenates blocks the way the
/// frontend does (dropping lead-in keys) and evaluates Hermite curves the way three.js's
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
    public float BlockDuration { get; private set; }
    public int BlockCount { get; private set; }
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
        r.BlockDuration = BinaryPrimitives.ReadSingleLittleEndian(d[16..]);
        int objectCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[20..]);
        int eventCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[24..]);
        r.BlockCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[28..]);
        int objectOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[32..]);
        int eventOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[36..]);
        int blockIndexOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[40..]);
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

        for (int b = 0; b < r.BlockCount; b++)
        {
            ReadOnlySpan<byte> idx = d.Slice(blockIndexOffset + (b * 16), 16);
            double start = BinaryPrimitives.ReadDoubleLittleEndian(idx);
            int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(idx[8..]);
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(idx[12..]);
            ReadOnlySpan<byte> block = d.Slice(offset, length);
            Assert.Equal(0, offset % 4);
            int trackCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(block);
            int p = 8;
            for (int t = 0; t < trackCount; t++)
            {
                ushort id = BinaryPrimitives.ReadUInt16LittleEndian(block[p..]);
                var channel = (ReplayChannel)block[p + 2];
                bool leadIn = (block[p + 3] & 1) != 0;
                int keys = (int)BinaryPrimitives.ReadUInt32LittleEndian(block[(p + 4)..]);
                p += 8;
                int dim = channel == ReplayChannel.Rotation ? 4 : 3;
                if (!r.Tracks.TryGetValue((id, channel), out Track? track))
                {
                    track = new Track { Dim = dim };
                    r.Tracks[(id, channel)] = track;
                }

                int timesAt = p;
                int valuesAt = timesAt + (keys * 4);
                int inAt = valuesAt + (keys * dim * 4);
                int outAt = inAt + (keys * dim * 4);
                p = outAt + (keys * dim * 4);
                for (int k = leadIn ? 1 : 0; k < keys; k++)
                {
                    track.Times.Add(start + BinaryPrimitives.ReadSingleLittleEndian(block[(timesAt + (k * 4))..]));
                    for (int c = 0; c < dim; c++)
                    {
                        track.Values.Add(BinaryPrimitives.ReadSingleLittleEndian(block[(valuesAt + (((k * dim) + c) * 4))..]));
                        track.In.Add(BinaryPrimitives.ReadSingleLittleEndian(block[(inAt + (((k * dim) + c) * 4))..]));
                        track.Out.Add(BinaryPrimitives.ReadSingleLittleEndian(block[(outAt + (((k * dim) + c) * 4))..]));
                    }
                }
            }
        }

        return r;
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
