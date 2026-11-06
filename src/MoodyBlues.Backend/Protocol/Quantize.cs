using System.Buffers.Binary;
using System.Numerics;

namespace MoodyBlues.Backend.Protocol;

/// <summary>
/// Dequantization math (Spec 9). Every value is <c>min + raw / (2^bits - 1) * (max - min)</c>,
/// the exact inverse of the client's linear remap. The formulas intentionally mirror the Unity
/// client's <c>SerializerDecode</c>, because the client baselines its deltas on what it decodes
/// from its own bytes: matching that arithmetic is what keeps reconstruction error bounded.
/// </summary>
public static class Quantize
{
    public static float Dequantize(uint value, int bits, float min, float max)
    {
        uint maxInt = (1u << bits) - 1u;
        float t = value / (float)maxInt;
        return min + t * (max - min);
    }

    public static float DecodeScalar16(ReadOnlySpan<byte> data, float min, float max) =>
        Dequantize(BinaryPrimitives.ReadUInt16LittleEndian(data), 16, min, max);

    public static float DecodeScalar8(byte raw, float min, float max) =>
        Dequantize(raw, 8, min, max);

    /// <summary>Spec 9.3: Vector3 as 3x 16-bit, x then y then z.</summary>
    public static Vector3 DecodeVector3_16(ReadOnlySpan<byte> data, float min, float max) => new(
        Dequantize(BinaryPrimitives.ReadUInt16LittleEndian(data), 16, min, max),
        Dequantize(BinaryPrimitives.ReadUInt16LittleEndian(data[2..]), 16, min, max),
        Dequantize(BinaryPrimitives.ReadUInt16LittleEndian(data[4..]), 16, min, max));

    /// <summary>Spec 9.4: Vector3 as 3x 8-bit.</summary>
    public static Vector3 DecodeVector3_8(ReadOnlySpan<byte> data, float min, float max) => new(
        Dequantize(data[0], 8, min, max),
        Dequantize(data[1], 8, min, max),
        Dequantize(data[2], 8, min, max));

    /// <summary>
    /// Spec 9.5: smallest-three quaternion, 4 bytes. Bits 0-1 hold the dropped index (x, y, z, w),
    /// then the other three components in ascending index order at 10 bits each. The dropped
    /// component is reconstructed as non-negative.
    /// </summary>
    public static Quaternion DecodeQuatSmallestThree(ReadOnlySpan<byte> data)
    {
        const float range = ProtocolConstants.TrueRotationComponentRange;
        uint packed = BinaryPrimitives.ReadUInt32LittleEndian(data);
        int droppedIndex = (int)(packed & 0b11);

        Span<float> c = stackalloc float[4];
        int shift = 2;
        float sumOfSquares = 0f;
        for (int i = 0; i < 4; i++)
        {
            if (i == droppedIndex)
            {
                continue;
            }

            float v = Dequantize((packed >> shift) & 0x3FF, 10, -range, range);
            c[i] = v;
            sumOfSquares += v * v;
            shift += 10;
        }

        c[droppedIndex] = MathF.Sqrt(MathF.Max(0f, 1f - sumOfSquares));
        return new Quaternion(c[0], c[1], c[2], c[3]);
    }

    /// <summary>Spec 9.8: drop-W quaternion, 3 bytes; W is reconstructed as non-negative.</summary>
    public static Quaternion DecodeQuatDropW(ReadOnlySpan<byte> data, float componentRange)
    {
        float x = Dequantize(data[0], 8, -componentRange, componentRange);
        float y = Dequantize(data[1], 8, -componentRange, componentRange);
        float z = Dequantize(data[2], 8, -componentRange, componentRange);
        float w = MathF.Sqrt(MathF.Max(0f, 1f - (x * x) - (y * y) - (z * z)));
        return new Quaternion(x, y, z, w);
    }

    /// <summary>
    /// Spec 9.6: single-axis rotation, 2 bytes. Bits 0-1 = axis (0=X, 1=Y, 2=Z), bits 2-15 = a 14-bit
    /// angle in degrees. Returns <c>(sin(theta/2) * axis, cos(theta/2))</c>, the same as Unity's
    /// <c>Quaternion.AngleAxis</c>.
    /// </summary>
    public static Quaternion DecodeAxisAngle(ReadOnlySpan<byte> data, float minDegrees, float maxDegrees)
    {
        ushort raw = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int axisId = raw & 0b11;
        float angleDeg = Dequantize((uint)((raw >> 2) & 0x3FFF), 14, minDegrees, maxDegrees);
        Vector3 axis = axisId switch
        {
            1 => Vector3.UnitY,
            2 => Vector3.UnitZ,
            0 => Vector3.UnitX,
            _ => throw new ProtocolException($"Invalid single-axis rotation axis id {axisId}."),
        };
        return Quaternion.CreateFromAxisAngle(axis, angleDeg * (MathF.PI / 180f));
    }

    /// <summary>Spec 9.7: delta position packed x11 / y10 / z11 into a uint32.</summary>
    public static Vector3 DecodeDeltaPosition(ReadOnlySpan<byte> data, float range)
    {
        uint packed = BinaryPrimitives.ReadUInt32LittleEndian(data);
        return new Vector3(
            Dequantize(packed & 0x7FF, 11, -range, range),
            Dequantize((packed >> 11) & 0x3FF, 10, -range, range),
            Dequantize((packed >> 21) & 0x7FF, 11, -range, range));
    }
}
