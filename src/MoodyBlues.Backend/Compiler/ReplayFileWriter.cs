using System.IO.Compression;
using System.Text;

namespace MoodyBlues.Backend.Compiler;

/// <summary>
/// Writes a compiled session as a MoodyBlues replay file ("MBRP", format version 3). The file is
/// brotli-compressed on disk and served with <c>Content-Encoding: br</c> (or decompressed on the
/// fly for clients that do not accept it), so viewers receive the layout below. All values are
/// little-endian. The header and the object, event, log and string tables are 4-byte aligned; the
/// track section that follows is a plain byte stream.
///
/// <code>
/// Header (80 bytes)
///   0  char[4] magic "MBRP"         4  u16 formatVersion (3)      6  u16 flags
///   8  f64 durationSeconds         16  u32 reserved (0)           20  u32 objectCount
///  24  u32 eventCount              28  u32 trackCount             32  u32 objectTableOffset
///  36  u32 eventTableOffset        40  u32 trackDataOffset        44  u32 totalKeyCount
///  48  u32 deltaPromotions         52  u32 hardClamps             56  u32 droppedEvents
///  60  u8  endReason (255 = none)  61  u8 reserved                62  u16 connectionCount
///  64  u32 logCount                68  u32 logTableOffset         72  u32 stringDataOffset
///  76  u32 stringDataLength
///   flags: bit0 endedCleanly, bit1 hardClamps &gt; 0, bit2 unknown objects were ignored
///
/// Sections, in file order: header, object table, event table, log table, string data (padded
/// with zeros to a 4-byte boundary), track data (runs to the end of the file).
///
/// Object table: objectCount x 20 bytes
///   u16 objectId, u8 kind (0 scene, 1 spawned), u8 channelMask (bit0 position, bit1 rotation,
///   bit2 scale), u16 templateObjectId, u16 initialParentObjectId, f64 spawnTime (-1 = scene
///   object), f32 deleteTime (-1 = never)
///
/// Event table: eventCount x 16 bytes, sorted by time
///   f64 time, u8 type, u8 aux, u16 objectId, u16 a, u16 b   (see <see cref="ReplayEventType"/>)
///
/// Log table: logCount x 20 bytes, sorted by time
///   f64 time, u8 level (1 info, 2 warning, 3 error), u8 color (0-63), u16 reserved,
///   u32 textOffset, u32 textLength   (byte range of UTF-8 text inside the string data)
///
/// String data: stringDataLength bytes of UTF-8
///
/// Track data: trackCount tracks in ascending (objectId, channel) order, each
///   varint   objectId
///   u8       channel (0 position, dim 3; 1 rotation, dim 4 = x, y, z, w; 2 scale, dim 3)
///   varint   keyCount (at least 1)
///   times    keyCount zigzag varints: deltas of tq = round(timeSeconds * 10000), first from 0
///   values   for each component c in 0..dim-1: keyCount zigzag varints, deltas (first from 0) of
///            round(value * G), G = 10000 for position and scale, 32768 for rotation
///   tangents for each component c in 0..dim-1, for each key k in 0..keyCount-1: a zigzag varint
///            of round(inTangent[k][c] * dt_(k-1) * G) when k &gt; 0, then one of
///            round(outTangent[k][c] * dt_k * G) when k &lt; keyCount - 1; not delta coded. Same G as
///            the values. dt_k = T[k+1] - T[k], where T[k] = tq[k] / 10000 are the decoded key
///            times, so each tangent is stored as the value change it contributes over its own
///            segment (the Hermite term m * dt) and its storage error on the curve is at most
///            about 0.15 * half a value step, whatever the key spacing. The first key's in-tangent
///            and the last key's out-tangent bound no segment and are not stored; readers set them
///            to 0. Readers compute tangent = q / G / dt in float64 and store it as float32; a
///            dt &lt;= 0 is a format error.
///   varint = unsigned LEB128 (7 bits per byte, low group first, high bit = continue), at most
///   8 bytes; zigzag(x) = (x &lt;&lt; 1) ^ (x &gt;&gt; 63). Every quantized integer is clamped to
///   +/-2^46 before delta coding and zigzag, so all of it stays inside a JavaScript safe integer
///   (readers must decode varints with arithmetic, not 32-bit bit operations).
/// </code>
///
/// Keys span the whole session. Values are decoded to float32 and times to float64 seconds; the
/// curve between keys is a cubic Hermite spline (three.js InterpolateBezier with control points
/// at one third of the key spacing), and rotations are normalized after interpolation. The
/// quantization grids (0.1 ms, 0.1 mm / 0.0001 scale, 2^-15 per quaternion component) are far
/// finer than the fitter's tolerance.
/// </summary>
public static class ReplayFileWriter
{
    public const ushort FormatVersion = 3;
    public const int HeaderSize = 80;
    public const int ObjectEntrySize = 20;
    public const int EventEntrySize = 16;
    public const int LogEntrySize = 20;

    public const double TimeGrid = 10000.0;
    public const double PositionScaleGrid = 10000.0;
    public const double RotationGrid = 32768.0;

    /// <summary>Quantized integers are clamped to this so deltas and zigzag stay inside 2^53.</summary>
    public const long QuantizedLimit = 1L << 46;

    private static readonly ReplayChannel[] Channels = [ReplayChannel.Position, ReplayChannel.Rotation, ReplayChannel.Scale];

    public static byte[] Serialize(CompiledReplay replay)
    {
        using var ms = new MemoryStream();
        Write(replay, ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Writes the replay brotli-compressed (quality 11, 16 MB window) and returns the uncompressed
    /// size, which the server reports to clients that need the decoded length up front.
    /// </summary>
    public static async Task<long> WriteBrotliAsync(CompiledReplay replay, Stream destination)
    {
        byte[] raw = Serialize(replay);
        var compressed = new byte[BrotliEncoder.GetMaxCompressedLength(raw.Length)];
        if (!BrotliEncoder.TryCompress(raw, compressed, out int written, quality: 11, window: 24))
        {
            throw new InvalidOperationException("Brotli compression of the replay failed.");
        }

        await destination.WriteAsync(compressed.AsMemory(0, written));
        return raw.Length;
    }

    public static void Write(CompiledReplay replay, Stream output)
    {
        var w = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        IReadOnlyList<ReplayObject> objects = replay.Objects;
        List<ReplayEvent> events = replay.Events.OrderBy(e => e.Time).ToList();

        // Stable sort, so logs written in the same tick keep their recorded order.
        List<ReplayLog> logs = replay.Logs.OrderBy(l => l.Time).ToList();
        var logTextOffsets = new uint[logs.Count];
        var logTextLengths = new uint[logs.Count];
        using var strings = new MemoryStream();
        for (int i = 0; i < logs.Count; i++)
        {
            byte[] text = Encoding.UTF8.GetBytes(logs[i].Text);
            logTextOffsets[i] = (uint)strings.Length;
            logTextLengths[i] = (uint)text.Length;
            strings.Write(text);
        }

        long stringDataLength = strings.Length;
        long paddedStringLength = (stringDataLength + 3) & ~3L;

        double duration = Math.Max(replay.DurationSeconds, LastKeyTime(objects));

        // Tracks are built first so the header can hold their count.
        using var trackData = new MemoryStream();
        uint trackCount = 0;
        foreach (ReplayObject o in objects.OrderBy(o => o.ObjectId))
        {
            foreach (ReplayChannel channel in Channels)
            {
                ChannelFitter? f = o.Channel(channel);
                if (f is { KeyCount: > 0 })
                {
                    WriteTrack(trackData, o.ObjectId, channel, f);
                    trackCount++;
                }
            }
        }

        long objectTableOffset = HeaderSize;
        long eventTableOffset = objectTableOffset + ((long)objects.Count * ObjectEntrySize);
        long logTableOffset = eventTableOffset + ((long)events.Count * EventEntrySize);
        long stringDataOffset = logTableOffset + ((long)logs.Count * LogEntrySize);
        long trackDataOffset = stringDataOffset + paddedStringLength;

        // Header
        w.Write("MBRP"u8);
        w.Write(FormatVersion);
        ushort flags = 0;
        if (replay.EndedCleanly) flags |= 1;
        if (replay.HardClamps > 0) flags |= 2;
        if (replay.UnknownObjectEvents > 0) flags |= 4;
        w.Write(flags);
        w.Write(duration);
        w.Write(0u);
        w.Write((uint)objects.Count);
        w.Write((uint)events.Count);
        w.Write(trackCount);
        w.Write((uint)objectTableOffset);
        w.Write((uint)eventTableOffset);
        w.Write((uint)trackDataOffset);
        w.Write((uint)Math.Min(uint.MaxValue, replay.KeyframeCount));
        w.Write(replay.DeltaPromotions);
        w.Write(replay.HardClamps);
        w.Write(replay.DroppedEvents);
        w.Write(replay.EndReason is { } reason ? (byte)reason : (byte)255);
        w.Write((byte)0);
        w.Write((ushort)Math.Min(ushort.MaxValue, replay.ConnectionCount));
        w.Write((uint)logs.Count);
        w.Write((uint)logTableOffset);
        w.Write((uint)stringDataOffset);
        w.Write((uint)stringDataLength);

        // Object table
        foreach (ReplayObject o in objects)
        {
            byte mask = 0;
            if (o.Position?.KeyCount > 0) mask |= 1;
            if (o.Rotation?.KeyCount > 0) mask |= 2;
            if (o.Scale?.KeyCount > 0) mask |= 4;
            w.Write(o.ObjectId);
            w.Write((byte)o.Kind);
            w.Write(mask);
            w.Write(o.TemplateObjectId);
            w.Write(o.InitialParentObjectId);
            w.Write(o.SpawnTime);
            w.Write((float)o.DeleteTime);
        }

        // Event table
        foreach (ReplayEvent e in events)
        {
            w.Write(e.Time);
            w.Write((byte)e.Type);
            w.Write(e.Aux);
            w.Write(e.ObjectId);
            w.Write(e.A);
            w.Write(e.B);
        }

        // Log table
        for (int i = 0; i < logs.Count; i++)
        {
            ReplayLog log = logs[i];
            w.Write(log.Time);
            w.Write(log.Level);
            w.Write((byte)(log.Color & 0x3F));
            w.Write((ushort)0);
            w.Write(logTextOffsets[i]);
            w.Write(logTextLengths[i]);
        }

        // String data, padded so the track data starts 4-byte aligned.
        w.Write(strings.GetBuffer(), 0, (int)stringDataLength);
        for (long pad = stringDataLength; pad < paddedStringLength; pad++)
        {
            w.Write((byte)0);
        }

        w.Write(trackData.GetBuffer(), 0, (int)trackData.Length);
        w.Flush();
    }

    private static double LastKeyTime(IReadOnlyList<ReplayObject> objects)
    {
        double last = 0;
        foreach (ReplayObject o in objects)
        {
            foreach (ChannelFitter? f in new[] { o.Position, o.Rotation, o.Scale })
            {
                if (f is { KeyCount: > 0 })
                {
                    last = Math.Max(last, f.KeyTimes[^1]);
                }
            }
        }

        return last;
    }

    private static void WriteTrack(Stream output, ushort objectId, ReplayChannel channel, ChannelFitter f)
    {
        int dim = f.Dimension;
        int keys = f.KeyCount;
        bool rotation = channel == ReplayChannel.Rotation;
        double valueGrid = rotation ? RotationGrid : PositionScaleGrid;

        WriteVarint(output, objectId);
        output.WriteByte((byte)channel);
        WriteVarint(output, (ulong)keys);

        var tq = new long[keys];
        long previous = 0;
        for (int k = 0; k < keys; k++)
        {
            tq[k] = QuantizeToInt(f.KeyTimes[k], TimeGrid);
            WriteZigzag(output, tq[k] - previous);
            previous = tq[k];
        }

        for (int c = 0; c < dim; c++)
        {
            previous = 0;
            for (int k = 0; k < keys; k++)
            {
                long q = QuantizeToInt(f.KeyValues[(k * dim) + c], valueGrid);
                WriteZigzag(output, q - previous);
                previous = q;
            }
        }

        // Tangents are stored as m * dt on the value grid, with dt from the decoded (quantized) times.
        for (int c = 0; c < dim; c++)
        {
            for (int k = 0; k < keys; k++)
            {
                if (k > 0)
                {
                    double dt = (tq[k] - tq[k - 1]) / TimeGrid;
                    WriteZigzag(output, QuantizeToInt(f.KeyInTangents[(k * dim) + c] * dt, valueGrid));
                }

                if (k < keys - 1)
                {
                    double dt = (tq[k + 1] - tq[k]) / TimeGrid;
                    WriteZigzag(output, QuantizeToInt(f.KeyOutTangents[(k * dim) + c] * dt, valueGrid));
                }
            }
        }
    }

    private static long QuantizeToInt(double value, double grid)
    {
        double scaled = Math.Round(value * grid);
        if (double.IsNaN(scaled))
        {
            return 0;
        }

        return (long)Math.Clamp(scaled, -QuantizedLimit, QuantizedLimit);
    }

    private static void WriteZigzag(Stream output, long value) =>
        WriteVarint(output, (ulong)((value << 1) ^ (value >> 63)));

    private static void WriteVarint(Stream output, ulong value)
    {
        while (value >= 0x80)
        {
            output.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        output.WriteByte((byte)value);
    }
}
