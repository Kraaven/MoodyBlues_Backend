using System.Buffers.Binary;
using System.Numerics;

namespace MoodyBlues.Backend.Protocol;

/// <summary>
/// Dequantization math (Spec.md Section 7).
///
/// All quantization maps a float range [min, max] to an unsigned integer of
/// <c>bits</c> bits via linear remap + round + clamp on the *encoder* side.
/// On the decoder side (this class) we only ever need the exact inverse remap.
/// </summary>
public static class Quantize
{
    public static float Dequantize(uint value, int bits, float min, float max)
    {
        uint maxInt = (1u << bits) - 1u;
        float t = value / (float)maxInt;
        return min + t * (max - min);
    }

    public static float DecodeScalar16(ReadOnlySpan<byte> data, (float Min, float Max) range)
    {
        ushort raw = BinaryPrimitives.ReadUInt16LittleEndian(data);
        return Dequantize(raw, 16, range.Min, range.Max);
    }

    public static float DecodeScalar8(byte raw, (float Min, float Max) range)
    {
        return Dequantize(raw, 8, range.Min, range.Max);
    }

    /// <summary>Section 7.4: Vector3 -> 3x 16-bit, x then y then z.</summary>
    public static Vector3 DecodeVector3_16(ReadOnlySpan<byte> data, (float Min, float Max) range)
    {
        ushort x = BinaryPrimitives.ReadUInt16LittleEndian(data);
        ushort y = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        ushort z = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        return new Vector3(
            Dequantize(x, 16, range.Min, range.Max),
            Dequantize(y, 16, range.Min, range.Max),
            Dequantize(z, 16, range.Min, range.Max));
    }

    /// <summary>Section 7.4: Vector3 -> 3x 8-bit, x then y then z.</summary>
    public static Vector3 DecodeVector3_8(ReadOnlySpan<byte> data, (float Min, float Max) range)
    {
        return new Vector3(
            Dequantize(data[0], 8, range.Min, range.Max),
            Dequantize(data[1], 8, range.Min, range.Max),
            Dequantize(data[2], 8, range.Min, range.Max));
    }

    /// <summary>
    /// Section 7.5: smallest-three quaternion, 4 bytes.
    /// Bits [0-1] = dropped index (0=x,1=y,2=z,3=w); then the other 3
    /// components in ascending index order, each 10 bits.
    /// </summary>
    public static Quaternion DecodeQuatSmallestThree(ReadOnlySpan<byte> data, (float Min, float Max) range)
    {
        uint packed = BinaryPrimitives.ReadUInt32LittleEndian(data);
        int droppedIndex = (int)(packed & 0b11);

        Span<float> components = stackalloc float[4];
        int shift = 2;
        for (int idx = 0; idx < 4; idx++)
        {
            if (idx == droppedIndex)
            {
                continue;
            }

            uint raw = (packed >> shift) & 0x3FF; // 10 bits
            components[idx] = Dequantize(raw, 10, range.Min, range.Max);
            shift += 10;
        }

        float sumOfSquares = 0f;
        for (int idx = 0; idx < 4; idx++)
        {
            if (idx != droppedIndex)
            {
                sumOfSquares += components[idx] * components[idx];
            }
        }

        components[droppedIndex] = MathF.Sqrt(MathF.Max(0f, 1f - sumOfSquares));

        return new Quaternion(components[0], components[1], components[2], components[3]);
    }

    /// <summary>Section 7.8: quaternion, drop-W, 3 bytes (x, y, z each 8-bit).</summary>
    public static Quaternion DecodeQuatDropW(ReadOnlySpan<byte> data, (float Min, float Max) range)
    {
        float x = Dequantize(data[0], 8, range.Min, range.Max);
        float y = Dequantize(data[1], 8, range.Min, range.Max);
        float z = Dequantize(data[2], 8, range.Min, range.Max);
        float w = MathF.Sqrt(MathF.Max(0f, 1f - (x * x) - (y * y) - (z * z)));
        return new Quaternion(x, y, z, w);
    }

    /// <summary>
    /// Section 7.6: rotation, single-axis, 2 bytes.
    /// Bits [0-1] = axis id (0=X,1=Y,2=Z), bits [2-15] = 14-bit quantized angle.
    /// </summary>
    public static (string Axis, float AngleDeg) DecodeAxisAngle(ReadOnlySpan<byte> data, (float Min, float Max) angleRange)
    {
        ushort raw = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int axisId = raw & 0b11;
        uint angleRaw = (uint)((raw >> 2) & 0x3FFF); // 14 bits
        float angle = Dequantize(angleRaw, 14, angleRange.Min, angleRange.Max);
        string axisName = axisId < ProtocolConstants.AxisNames.Length
            ? ProtocolConstants.AxisNames[axisId]
            : $"?{axisId}";
        return (axisName, angle);
    }

    /// <summary>Section 7.7: delta position packed x11/y10/z11, 4 bytes.</summary>
    public static Vector3 DecodeDeltaPosition(ReadOnlySpan<byte> data, (float Min, float Max) range)
    {
        uint packed = BinaryPrimitives.ReadUInt32LittleEndian(data);
        uint xRaw = packed & 0x7FF; // 11 bits
        uint yRaw = (packed >> 11) & 0x3FF; // 10 bits
        uint zRaw = (packed >> 21) & 0x7FF; // 11 bits
        return new Vector3(
            Dequantize(xRaw, 11, range.Min, range.Max),
            Dequantize(yRaw, 10, range.Min, range.Max),
            Dequantize(zRaw, 11, range.Min, range.Max));
    }
}
