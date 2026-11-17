using System.Buffers.Binary;
using System.Numerics;

namespace MoodyBlues.Backend.Protocol;

/// <summary>
/// Dequantization math (Spec 9). Signed (+/-R) fields are symmetric: an n-bit field has
/// <c>L = 2^(n-1) - 1</c>, step <c>R / (float)L</c>, and the stored unsigned code is <c>q + L</c>
/// for <c>q</c> in <c>[-L, L]</c>, so zero is exactly representable and the all-ones code is never
/// emitted. Unsigned fields (absolute scale, absolute single-axis angle) are a plain linear remap of
/// <c>[min, max]</c> onto <c>[0, 2^n - 1]</c>. The float32 arithmetic intentionally mirrors the Unity
/// client's <c>SerializerDecode</c>, because the client baselines its deltas on what it decodes from
/// its own bytes: matching it bit for bit is what keeps reconstruction error bounded.
/// </summary>
public static class Quantize
{
    /// <summary>Unsigned linear remap: <c>min + raw / (2^bits - 1) * (max - min)</c>.</summary>
    public static float Dequantize(uint value, int bits, float min, float max)
    {
        uint maxInt = (1u << bits) - 1u;
        float t = value / (float)maxInt;
        return min + t * (max - min);
    }

    /// <summary>Largest code magnitude of a symmetric n-bit field: <c>2^(n-1) - 1</c>.</summary>
    public static int SymmetricLevels(int bits) => (1 << (bits - 1)) - 1;

    /// <summary>
    /// Symmetric decode: <c>q = (int)u - L; v = (float)q * (range / (float)L)</c>. The all-ones code
    /// (<c>2L + 1</c>) is never emitted by a conforming client, so it is a protocol error.
    /// </summary>
    public static float DequantizeSymmetric(uint value, int bits, float range)
    {
        int levels = SymmetricLevels(bits);
        if (value > 2u * (uint)levels)
        {
            throw new ProtocolException($"Invalid {bits}-bit quantized value {value} (reserved all-ones code).");
        }

        float step = (float)(range / (float)levels);
        return (float)((float)((int)value - levels) * step);
    }

    public static float DecodeScalar16(ReadOnlySpan<byte> data, float min, float max) =>
        Dequantize(BinaryPrimitives.ReadUInt16LittleEndian(data), 16, min, max);

    public static float DecodeScalar8(byte raw, float range) =>
        DequantizeSymmetric(raw, 8, range);

    /// <summary>Spec 9.3: absolute position, 3x 16-bit symmetric over +/-range, x then y then z.</summary>
    public static Vector3 DecodePosition(ReadOnlySpan<byte> data, float range) => new(
        DequantizeSymmetric(BinaryPrimitives.ReadUInt16LittleEndian(data), 16, range),
        DequantizeSymmetric(BinaryPrimitives.ReadUInt16LittleEndian(data[2..]), 16, range),
        DequantizeSymmetric(BinaryPrimitives.ReadUInt16LittleEndian(data[4..]), 16, range));

    /// <summary>Spec 9.3: absolute scale, 3x 16-bit unsigned over <c>[min, max]</c>.</summary>
    public static Vector3 DecodeScale(ReadOnlySpan<byte> data, float min, float max) => new(
        Dequantize(BinaryPrimitives.ReadUInt16LittleEndian(data), 16, min, max),
        Dequantize(BinaryPrimitives.ReadUInt16LittleEndian(data[2..]), 16, min, max),
        Dequantize(BinaryPrimitives.ReadUInt16LittleEndian(data[4..]), 16, min, max));

    /// <summary>Spec 9.4: delta scale, 3x 8-bit symmetric over +/-range.</summary>
    public static Vector3 DecodeDeltaScale(ReadOnlySpan<byte> data, float range) => new(
        DequantizeSymmetric(data[0], 8, range),
        DequantizeSymmetric(data[1], 8, range),
        DequantizeSymmetric(data[2], 8, range));

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

            float v = DequantizeSymmetric((packed >> shift) & 0x3FF, 10, range);
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
        float x = DequantizeSymmetric(data[0], 8, componentRange);
        float y = DequantizeSymmetric(data[1], 8, componentRange);
        float z = DequantizeSymmetric(data[2], 8, componentRange);
        float w = MathF.Sqrt(MathF.Max(0f, 1f - (x * x) - (y * y) - (z * z)));
        return new Quaternion(x, y, z, w);
    }

    /// <summary>
    /// Spec 9.6: single-axis rotation, 2 bytes. Bits 0-1 = axis (0=X, 1=Y, 2=Z), bits 2-15 = a 14-bit
    /// angle in degrees: unsigned over 0..360 for absolute rotations (<paramref name="symmetric"/>
    /// false), symmetric over +/-<paramref name="range"/> for deltas. Returns
    /// <c>(sin(theta/2) * axis, cos(theta/2))</c>, the same as Unity's <c>Quaternion.AngleAxis</c>.
    /// </summary>
    public static Quaternion DecodeAxisAngle(ReadOnlySpan<byte> data, bool symmetric, float range)
    {
        ushort raw = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int axisId = raw & 0b11;
        uint code = (uint)((raw >> 2) & 0x3FFF);
        float angleDeg = symmetric
            ? DequantizeSymmetric(code, 14, range)
            : Dequantize(code, 14, ProtocolConstants.TrueSingleAxisAngleMin, ProtocolConstants.TrueSingleAxisAngleMax);
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
            DequantizeSymmetric(packed & 0x7FF, 11, range),
            DequantizeSymmetric((packed >> 11) & 0x3FF, 10, range),
            DequantizeSymmetric((packed >> 21) & 0x7FF, 11, range));
    }
}
