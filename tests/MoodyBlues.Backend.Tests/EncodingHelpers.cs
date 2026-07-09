using System.Buffers.Binary;
using System.Numerics;
using MoodyBlues.Backend.Protocol;

namespace MoodyBlues.Backend.Tests;

/// <summary>
/// Test-only encoders that mirror Spec.md Section 7's quantization scheme.
///
/// These exist purely so the test suite can build known-good binary fixtures
/// to exercise the real decoder (<see cref="EventParser"/>) without needing
/// a live Unity client. They intentionally duplicate the *encode* side of
/// the math whose *decode* side lives in <see cref="Quantize"/>.
/// </summary>
public static class EncodingHelpers
{
    public static uint QuantizeValue(float value, int bits, float min, float max)
    {
        uint maxInt = (1u << bits) - 1u;
        float t = (value - min) / (max - min);
        t = Math.Clamp(t, 0f, 1f);
        return (uint)MathF.Round(t * maxInt);
    }

    public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    public static byte[] Envelope(EventType type, ushort objectId)
    {
        var buf = new byte[3];
        buf[0] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(1), objectId);
        return buf;
    }

    public static byte[] TimeStampBytes(double seconds)
    {
        var buf = new byte[9];
        buf[0] = (byte)EventType.TimeStamp;
        BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(1), seconds);
        return buf;
    }

    public static byte[] PackVector3_16(Vector3 v, (float Min, float Max) range)
    {
        var buf = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0), (ushort)QuantizeValue(v.X, 16, range.Min, range.Max));
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), (ushort)QuantizeValue(v.Y, 16, range.Min, range.Max));
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), (ushort)QuantizeValue(v.Z, 16, range.Min, range.Max));
        return buf;
    }

    public static byte[] PackVector3_8(Vector3 v, (float Min, float Max) range) =>
        [(byte)QuantizeValue(v.X, 8, range.Min, range.Max), (byte)QuantizeValue(v.Y, 8, range.Min, range.Max), (byte)QuantizeValue(v.Z, 8, range.Min, range.Max)];

    public static byte[] PackScalar16(float value, (float Min, float Max) range)
    {
        var buf = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)QuantizeValue(value, 16, range.Min, range.Max));
        return buf;
    }

    public static byte[] PackScalar8(float value, (float Min, float Max) range) =>
        [(byte)QuantizeValue(value, 8, range.Min, range.Max)];

    /// <summary>Section 7.5 encode: smallest-three quaternion, 4 bytes.</summary>
    public static byte[] PackQuatSmallestThree(Quaternion q, (float Min, float Max) range)
    {
        Span<float> comps = [q.X, q.Y, q.Z, q.W];

        int droppedIndex = 0;
        float maxAbs = MathF.Abs(comps[0]);
        for (int i = 1; i < 4; i++)
        {
            if (MathF.Abs(comps[i]) > maxAbs)
            {
                maxAbs = MathF.Abs(comps[i]);
                droppedIndex = i;
            }
        }

        if (comps[droppedIndex] < 0)
        {
            for (int i = 0; i < 4; i++)
            {
                comps[i] = -comps[i];
            }
        }

        uint packed = (uint)droppedIndex;
        int shift = 2;
        for (int i = 0; i < 4; i++)
        {
            if (i == droppedIndex)
            {
                continue;
            }

            uint q10 = QuantizeValue(comps[i], 10, range.Min, range.Max) & 0x3FF;
            packed |= q10 << shift;
            shift += 10;
        }

        var buf = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, packed);
        return buf;
    }

    /// <summary>Section 7.8 encode: quaternion, drop-W, 3 bytes.</summary>
    public static byte[] PackQuatDropW(Quaternion q, (float Min, float Max) range)
    {
        float x = q.X;
        float y = q.Y;
        float z = q.Z;
        float w = q.W;
        if (w < 0)
        {
            x = -x;
            y = -y;
            z = -z;
        }

        return [(byte)QuantizeValue(x, 8, range.Min, range.Max), (byte)QuantizeValue(y, 8, range.Min, range.Max), (byte)QuantizeValue(z, 8, range.Min, range.Max)];
    }

    public static byte[] PackAxisAngle(int axisId, float angle, (float Min, float Max) angleRange)
    {
        uint q = QuantizeValue(angle, 14, angleRange.Min, angleRange.Max) & 0x3FFF;
        ushort raw = (ushort)((uint)(axisId & 0b11) | (q << 2));
        var buf = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, raw);
        return buf;
    }

    /// <summary>Section 7.7 encode: delta position packed x11/y10/z11, 4 bytes.</summary>
    public static byte[] PackDeltaPosition(Vector3 v, (float Min, float Max) range)
    {
        uint qx = QuantizeValue(v.X, 11, range.Min, range.Max) & 0x7FF;
        uint qy = QuantizeValue(v.Y, 10, range.Min, range.Max) & 0x3FF;
        uint qz = QuantizeValue(v.Z, 11, range.Min, range.Max) & 0x7FF;
        uint packed = qx | (qy << 11) | (qz << 21);
        var buf = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, packed);
        return buf;
    }
}
