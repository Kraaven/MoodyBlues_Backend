using System.Buffers.Binary;
using System.Numerics;
using MoodyBlues.Backend.Protocol;

namespace MoodyBlues.Backend.Tests;

/// <summary>
/// Test-only encoders that mirror Spec.md Section 9's quantization scheme.
///
/// These exist purely so the test suite can build known-good binary fixtures
/// to exercise the real decoder (<see cref="EventParser"/>) without needing
/// a live Unity client. They intentionally duplicate the *encode* side of
/// the math whose *decode* side lives in <see cref="Quantize"/>.
/// </summary>
public static class EncodingHelpers
{
    /// <summary>Unsigned linear remap of <c>[min, max]</c> onto <c>[0, 2^bits - 1]</c> (absolute scale, absolute single-axis angle).</summary>
    public static uint QuantizeValue(float value, int bits, float min, float max)
    {
        uint maxInt = (1u << bits) - 1u;
        float t = (value - min) / (max - min);
        t = Math.Clamp(t, 0f, 1f);
        return (uint)MathF.Round(t * maxInt);
    }

    /// <summary>Symmetric encode: <c>q = clamp(round(v / step), -L, L)</c>, stored as <c>q + L</c>.</summary>
    public static uint QuantizeSymmetric(float value, int bits, float range)
    {
        int levels = Quantize.SymmetricLevels(bits);
        float step = (float)(range / (float)levels);
        int q = Math.Clamp((int)MathF.Round(value / step), -levels, levels);
        return (uint)(q + levels);
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

    public static byte[] PackPosition(Vector3 v, float range)
    {
        var buf = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0), (ushort)QuantizeSymmetric(v.X, 16, range));
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), (ushort)QuantizeSymmetric(v.Y, 16, range));
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), (ushort)QuantizeSymmetric(v.Z, 16, range));
        return buf;
    }

    public static byte[] PackScale(Vector3 v, (float Min, float Max) range)
    {
        var buf = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0), (ushort)QuantizeValue(v.X, 16, range.Min, range.Max));
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), (ushort)QuantizeValue(v.Y, 16, range.Min, range.Max));
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), (ushort)QuantizeValue(v.Z, 16, range.Min, range.Max));
        return buf;
    }

    public static byte[] PackDeltaScale(Vector3 v, float range) =>
        [(byte)QuantizeSymmetric(v.X, 8, range), (byte)QuantizeSymmetric(v.Y, 8, range), (byte)QuantizeSymmetric(v.Z, 8, range)];

    public static byte[] PackScalar16(float value, (float Min, float Max) range)
    {
        var buf = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)QuantizeValue(value, 16, range.Min, range.Max));
        return buf;
    }

    public static byte[] PackDeltaScalar8(float value, float range) =>
        [(byte)QuantizeSymmetric(value, 8, range)];

    /// <summary>Section 7.5 encode: smallest-three quaternion, 4 bytes.</summary>
    public static byte[] PackQuatSmallestThree(Quaternion q, float range)
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

            uint q10 = QuantizeSymmetric(comps[i], 10, range) & 0x3FF;
            packed |= q10 << shift;
            shift += 10;
        }

        var buf = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, packed);
        return buf;
    }

    /// <summary>Section 7.8 encode: quaternion, drop-W, 3 bytes.</summary>
    public static byte[] PackQuatDropW(Quaternion q, float range)
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

        return [(byte)QuantizeSymmetric(x, 8, range), (byte)QuantizeSymmetric(y, 8, range), (byte)QuantizeSymmetric(z, 8, range)];
    }

    /// <summary>Delta single-axis rotation: 14-bit symmetric angle over +/-range degrees.</summary>
    public static byte[] PackAxisAngle(int axisId, float angle, float range)
    {
        uint q = QuantizeSymmetric(angle, 14, range) & 0x3FFF;
        ushort raw = (ushort)((uint)(axisId & 0b11) | (q << 2));
        var buf = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, raw);
        return buf;
    }

    /// <summary>Section 7.7 encode: delta position packed x11/y10/z11, 4 bytes.</summary>
    public static byte[] PackDeltaPosition(Vector3 v, float range)
    {
        uint qx = QuantizeSymmetric(v.X, 11, range) & 0x7FF;
        uint qy = QuantizeSymmetric(v.Y, 10, range) & 0x3FF;
        uint qz = QuantizeSymmetric(v.Z, 11, range) & 0x7FF;
        uint packed = qx | (qy << 11) | (qz << 21);
        var buf = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, packed);
        return buf;
    }
}
