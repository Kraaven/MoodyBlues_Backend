using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

namespace MoodyBlues.Backend.Storage;

/// <summary>Kinds of record in a raw session log.</summary>
public enum RawLogRecordKind : byte
{
    /// <summary>A WebSocket connection was accepted. Payload: u32 connection index (0-based).</summary>
    ConnectionOpened = 1,

    /// <summary>One complete, validated WebSocket message exactly as received.</summary>
    Message = 2,

    /// <summary>A connection ended. Payload: u8 <see cref="RawLogCloseReason"/>.</summary>
    ConnectionClosed = 3,

    /// <summary>A message that failed validation and was not applied. Payload: the bytes as received.</summary>
    RejectedMessage = 4,
}

public enum RawLogCloseReason : byte
{
    ClientClosed = 0,
    InactivityTimeout = 1,
    Error = 2,
    ServerShutdown = 3,
}

/// <summary>One record read back from a raw log. <see cref="Connection"/> is the index of the connection the record belongs to.</summary>
public readonly record struct RawLogRecord(RawLogRecordKind Kind, long ReceivedUnixMs, ReadOnlyMemory<byte> Payload, uint Connection);

/// <summary>
/// The append-only raw log of a session: every validated WebSocket message the client sent, in
/// order, with connection boundaries. It is the source of truth for the session; the compiled
/// replay file is derived from it and can be rebuilt at any time.
///
/// Layout: the 8-byte magic <c>"MBRAWv02"</c>, then little-endian records
/// <c>[u8 kind][u32 payloadLength][i64 receivedUnixMs][u32 connection][payload]</c>. The
/// connection index lets the compiler keep each connection's messages apart: a client that
/// reconnects before the server notices its old connection died has two open at once, and their
/// records interleave. No other format is read. A crash can leave a truncated final record;
/// <see cref="RawLogReader"/> ignores it.
/// </summary>
public static class RawLogFormat
{
    public static readonly byte[] Magic = Encoding.ASCII.GetBytes("MBRAWv02");

    public const int MagicSize = 8;
    public const int RecordHeaderSize = 1 + 4 + 8 + 4;

    /// <summary>Records larger than this are treated as corruption.</summary>
    public const int MaxRecordPayload = 16 * 1024 * 1024;

    public static bool IsMagic(ReadOnlySpan<byte> magic) => magic.SequenceEqual(Magic);
}

/// <summary>
/// Appends records to one session's raw log. A session can briefly have two connections open
/// (the client reconnecting before the server noticed the old one died), so writes are serialised
/// per session through <see cref="RawLogWriterRegistry"/>, and each record carries its connection.
/// </summary>
public sealed class RawLogWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly object _sync = new();
    private readonly byte[] _header = new byte[RawLogFormat.RecordHeaderSize];
    private uint _lastOpenedConnection;

    internal int RefCount;

    public RawLogWriter(string fullPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _stream = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (_stream.Length == 0)
        {
            _stream.Write(RawLogFormat.Magic);
        }
        else
        {
            var magic = new byte[RawLogFormat.MagicSize];
            _stream.ReadExactly(magic);
            if (!RawLogFormat.IsMagic(magic))
            {
                _stream.Dispose();
                throw new InvalidDataException($"Not a current MoodyBlues raw log (bad or outdated magic): {fullPath}");
            }

            // A crash can leave a half-written final record. Appending after it would hide every
            // later record from the reader, so cut the file back to the last complete record.
            long validEnd = RawLogReader.FindValidEnd(fullPath);
            if (validEnd < _stream.Length)
            {
                _stream.SetLength(validEnd);
            }

            _stream.Seek(0, SeekOrigin.End);
        }
    }

    public long Length
    {
        get
        {
            lock (_sync)
            {
                return _stream.Length;
            }
        }
    }

    /// <summary>Appends a record for the most recently opened connection.</summary>
    public void Append(RawLogRecordKind kind, ReadOnlySpan<byte> payload) =>
        Append(kind, payload, _lastOpenedConnection);

    public void Append(RawLogRecordKind kind, ReadOnlySpan<byte> payload, uint connection)
    {
        lock (_sync)
        {
            _header[0] = (byte)kind;
            BinaryPrimitives.WriteUInt32LittleEndian(_header.AsSpan(1), (uint)payload.Length);
            BinaryPrimitives.WriteInt64LittleEndian(_header.AsSpan(5), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            BinaryPrimitives.WriteUInt32LittleEndian(_header.AsSpan(13), connection);

            _stream.Write(_header);
            _stream.Write(payload);

            // Hand the bytes to the OS on every record so a process crash loses nothing that was
            // acknowledged; a full fsync per message would be needlessly slow at capture rate.
            _stream.Flush(flushToDisk: false);
        }
    }

    public void AppendConnectionOpened(uint connectionIndex)
    {
        Span<byte> payload = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, connectionIndex);
        lock (_sync)
        {
            _lastOpenedConnection = connectionIndex;
            Append(RawLogRecordKind.ConnectionOpened, payload, connectionIndex);
        }
    }

    /// <summary>Closes the most recently opened connection.</summary>
    public void AppendConnectionClosed(RawLogCloseReason reason) =>
        AppendConnectionClosed(reason, _lastOpenedConnection);

    public void AppendConnectionClosed(RawLogCloseReason reason, uint connection)
    {
        Span<byte> payload = [(byte)reason];
        Append(RawLogRecordKind.ConnectionClosed, payload, connection);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _stream.Flush(flushToDisk: true);
            _stream.Dispose();
        }
    }
}

/// <summary>Shares one <see cref="RawLogWriter"/> per session file across concurrent connections.</summary>
public sealed class RawLogWriterRegistry
{
    private readonly ConcurrentDictionary<string, RawLogWriter> _writers = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public RawLogWriter Acquire(string fullPath)
    {
        lock (_sync)
        {
            if (!_writers.TryGetValue(fullPath, out RawLogWriter? writer))
            {
                writer = new RawLogWriter(fullPath);
                _writers[fullPath] = writer;
            }

            writer.RefCount++;
            return writer;
        }
    }

    public void Release(string fullPath)
    {
        lock (_sync)
        {
            if (_writers.TryGetValue(fullPath, out RawLogWriter? writer) && --writer.RefCount <= 0)
            {
                _writers.TryRemove(fullPath, out _);
                writer.Dispose();
            }
        }
    }
}

/// <summary>Reads a raw log sequentially, tolerating a truncated final record.</summary>
public static class RawLogReader
{
    public static IEnumerable<RawLogRecord> Read(string fullPath)
    {
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        foreach (RawLogRecord record in Read(stream))
        {
            yield return record;
        }
    }

    public static IEnumerable<RawLogRecord> Read(Stream stream)
    {
        var magic = new byte[RawLogFormat.MagicSize];
        if (!TryReadExactly(stream, magic) || !RawLogFormat.IsMagic(magic))
        {
            throw new InvalidDataException("Not a current MoodyBlues raw log (bad or outdated magic).");
        }

        var header = new byte[RawLogFormat.RecordHeaderSize];
        while (TryReadExactly(stream, header))
        {
            var kind = (RawLogRecordKind)header[0];
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1));
            long received = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(5));
            uint connection = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(13));
            if (length > RawLogFormat.MaxRecordPayload)
            {
                yield break; // corrupt length; treat as end of usable data
            }

            var payload = new byte[length];
            if (!TryReadExactly(stream, payload))
            {
                yield break; // truncated final record from a crash
            }

            yield return new RawLogRecord(kind, received, payload, connection);
        }
    }

    /// <summary>Byte offset just past the last complete record (or the magic, for an empty log).</summary>
    public static long FindValidEnd(string fullPath)
    {
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        var magic = new byte[RawLogFormat.MagicSize];
        if (!TryReadExactly(stream, magic) || !RawLogFormat.IsMagic(magic))
        {
            throw new InvalidDataException($"Not a current MoodyBlues raw log (bad or outdated magic): {fullPath}");
        }

        long validEnd = stream.Position;
        var header = new byte[RawLogFormat.RecordHeaderSize];
        while (TryReadExactly(stream, header))
        {
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1));
            if (length > RawLogFormat.MaxRecordPayload || stream.Position + length > stream.Length)
            {
                break;
            }

            stream.Seek(length, SeekOrigin.Current);
            validEnd = stream.Position;
        }

        return validEnd;
    }

    private static bool TryReadExactly(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }
}
