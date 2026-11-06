using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MoodyBlues.Backend.Compiler;

/// <summary>
/// Writes a compiled session as a MoodyBlues replay file ("MBRP", format version 1). The file is
/// gzipped on disk and served with <c>Content-Encoding: gzip</c>, so browsers receive the raw
/// layout below. All values are little-endian and every section starts on a 4-byte boundary so
/// readers can view float arrays in place.
///
/// <code>
/// Header (64 bytes)
///   0  char[4] magic "MBRP"         4  u16 formatVersion (1)      6  u16 flags
///   8  f64 durationSeconds         16  f32 blockDurationSeconds  20  u32 objectCount
///  24  u32 eventCount              28  u32 blockCount            32  u32 objectTableOffset
///  36  u32 eventTableOffset        40  u32 blockIndexOffset      44  u32 totalKeyCount
///  48  u32 deltaPromotions         52  u32 hardClamps            56  u32 droppedEvents
///  60  u8  endReason (255 = none)  61  u8 reserved               62  u16 connectionCount
///   flags: bit0 endedCleanly, bit1 hardClamps &gt; 0, bit2 unknown objects were ignored
///
/// Object table: objectCount x 20 bytes
///   u16 objectId, u8 kind (0 scene, 1 spawned), u8 channelMask (bit0 position, bit1 rotation,
///   bit2 scale), u16 templateObjectId, u16 initialParentObjectId, f64 spawnTime (-1 = scene
///   object), f32 deleteTime (-1 = never)
///
/// Event table: eventCount x 16 bytes, sorted by time
///   f64 time, u8 type, u8 aux, u16 objectId, u16 a, u16 b   (see <see cref="ReplayEventType"/>)
///
/// Block index: blockCount x 16 bytes
///   f64 blockStartTime, u32 byteOffset, u32 byteLength
///
/// Block: u32 trackCount, u32 reserved, then per track:
///   u16 objectId, u8 channel (0 position, 1 rotation, 2 scale), u8 flags (bit0: first key is a
///   lead-in copied from before the block), u32 keyCount,
///   f32 times[keyCount] (seconds relative to blockStartTime; a lead-in is negative),
///   f32 values[keyCount * dim], f32 inTangents[keyCount * dim], f32 outTangents[keyCount * dim]
///   dim = 3 for position/scale, 4 for rotation (x, y, z, w). Tangents are units per second.
/// </code>
///
/// Blocks exist so a viewer can later fetch only the part of a long session it needs (HTTP Range
/// on the uncompressed file). Each block repeats, as a lead-in, the last key before it of every
/// track still alive, which makes every block playable on its own. A viewer that loads the whole
/// file drops the lead-ins and concatenates the blocks.
/// </summary>
public static class ReplayFileWriter
{
    public const ushort FormatVersion = 1;
    public const double DefaultBlockDurationSeconds = 60.0;
    public const int HeaderSize = 64;
    public const int ObjectEntrySize = 20;
    public const int EventEntrySize = 16;
    public const int BlockIndexEntrySize = 16;

    public static byte[] Serialize(CompiledReplay replay, double blockDuration = DefaultBlockDurationSeconds)
    {
        using var ms = new MemoryStream();
        Write(replay, ms, blockDuration);
        return ms.ToArray();
    }

    public static async Task WriteGzipAsync(CompiledReplay replay, Stream destination, double blockDuration = DefaultBlockDurationSeconds)
    {
        byte[] raw = Serialize(replay, blockDuration);
        await using var gzip = new GZipStream(destination, CompressionLevel.Optimal, leaveOpen: true);
        await gzip.WriteAsync(raw);
    }

    public static void Write(CompiledReplay replay, Stream output, double blockDuration = DefaultBlockDurationSeconds)
    {
        var w = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        IReadOnlyList<ReplayObject> objects = replay.Objects;
        List<ReplayEvent> events = replay.Events.OrderBy(e => e.Time).ToList();

        double duration = Math.Max(replay.DurationSeconds, LastKeyTime(objects));
        int blockCount = Math.Max(1, (int)Math.Ceiling(duration / blockDuration));

        long objectTableOffset = HeaderSize;
        long eventTableOffset = objectTableOffset + ((long)objects.Count * ObjectEntrySize);
        long blockIndexOffset = eventTableOffset + ((long)events.Count * EventEntrySize);
        long firstBlockOffset = blockIndexOffset + ((long)blockCount * BlockIndexEntrySize);

        // Header
        w.Write("MBRP"u8);
        w.Write(FormatVersion);
        ushort flags = 0;
        if (replay.EndedCleanly) flags |= 1;
        if (replay.HardClamps > 0) flags |= 2;
        if (replay.UnknownObjectEvents > 0) flags |= 4;
        w.Write(flags);
        w.Write(duration);
        w.Write((float)blockDuration);
        w.Write((uint)objects.Count);
        w.Write((uint)events.Count);
        w.Write((uint)blockCount);
        w.Write((uint)objectTableOffset);
        w.Write((uint)eventTableOffset);
        w.Write((uint)blockIndexOffset);
        w.Write((uint)Math.Min(uint.MaxValue, replay.KeyframeCount));
        w.Write(replay.DeltaPromotions);
        w.Write(replay.HardClamps);
        w.Write(replay.DroppedEvents);
        w.Write(replay.EndReason is { } reason ? (byte)reason : (byte)255);
        w.Write((byte)0);
        w.Write((ushort)Math.Min(ushort.MaxValue, replay.ConnectionCount));

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

        // Blocks are built in memory first so the index can hold their offsets.
        var blocks = new List<byte[]>(blockCount);
        for (int b = 0; b < blockCount; b++)
        {
            blocks.Add(BuildBlock(objects, b * blockDuration, (b + 1) * blockDuration, isLast: b == blockCount - 1));
        }

        long offset = firstBlockOffset;
        for (int b = 0; b < blockCount; b++)
        {
            w.Write(b * blockDuration);
            w.Write((uint)offset);
            w.Write((uint)blocks[b].Length);
            offset += blocks[b].Length;
        }

        foreach (byte[] block in blocks)
        {
            w.Write(block);
        }

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

    private static byte[] BuildBlock(IReadOnlyList<ReplayObject> objects, double start, double end, bool isLast)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0u); // trackCount, patched below
        w.Write(0u);
        uint trackCount = 0;

        foreach (ReplayObject o in objects)
        {
            bool deletedBefore = o.DeleteTime >= 0 && o.DeleteTime <= start;
            bool spawnedAfter = !isLast && o.SpawnTime >= end;
            if (deletedBefore || spawnedAfter)
            {
                continue;
            }

            foreach (ReplayChannel channel in new[] { ReplayChannel.Position, ReplayChannel.Rotation, ReplayChannel.Scale })
            {
                ChannelFitter? f = o.Channel(channel);
                if (f is not { KeyCount: > 0 })
                {
                    continue;
                }

                // Keys with start <= t < end (the last block also takes keys at exactly 'end'),
                // preceded by the last earlier key as a lead-in so the block stands alone.
                int first = LowerBound(f.KeyTimes, start);
                int stop = isLast ? f.KeyCount : LowerBound(f.KeyTimes, end);
                bool leadIn = first > 0;
                int from = leadIn ? first - 1 : first;
                int count = stop - from;
                if (count <= 0)
                {
                    continue;
                }

                int dim = f.Dimension;
                w.Write(o.ObjectId);
                w.Write((byte)channel);
                w.Write(leadIn ? (byte)1 : (byte)0);
                w.Write((uint)count);
                for (int k = from; k < stop; k++)
                {
                    w.Write((float)(f.KeyTimes[k] - start));
                }

                WriteRange(w, f.KeyValues, from * dim, count * dim);
                WriteRange(w, f.KeyInTangents, from * dim, count * dim);
                WriteRange(w, f.KeyOutTangents, from * dim, count * dim);
                trackCount++;
            }
        }

        w.Flush();
        byte[] bytes = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, trackCount);
        return bytes;
    }

    private static void WriteRange(BinaryWriter w, List<float> values, int offset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            w.Write(values[offset + i]);
        }
    }

    private static int LowerBound(List<double> times, double value)
    {
        int lo = 0, hi = times.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (times[mid] < value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
