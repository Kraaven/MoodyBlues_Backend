using System.Buffers.Binary;
using System.Numerics;
using MoodyBlues.Backend.Protocol;

namespace MoodyBlues.Backend.Tests;

/// <summary>Protocol v4 symmetric quantization: zero is exact, the all-ones code is invalid.</summary>
public class QuantizeTests
{
    [Theory]
    [InlineData(8, 0.25f)]
    [InlineData(10, 0.70711f)]
    [InlineData(11, 1.5f)]
    [InlineData(14, 2f)]
    [InlineData(16, 500f)]
    public void Symmetric_ZeroIsExact_AndExtremesAreMinusAndPlusRange(int bits, float range)
    {
        uint levels = (uint)Quantize.SymmetricLevels(bits);

        Assert.Equal(0f, Quantize.DequantizeSymmetric(levels, bits, range));
        Assert.Equal(-range, Quantize.DequantizeSymmetric(0, bits, range), 5);
        Assert.Equal(range, Quantize.DequantizeSymmetric(2 * levels, bits, range), 5);
        Assert.Equal(levels, EncodingHelpers.QuantizeSymmetric(0f, bits, range));
    }

    [Theory]
    [InlineData(8, 127)]
    [InlineData(10, 511)]
    [InlineData(11, 1023)]
    [InlineData(14, 8191)]
    [InlineData(16, 32767)]
    public void SymmetricLevels_AreTwoToTheNMinusOneMinusOne(int bits, int expected) =>
        Assert.Equal(expected, Quantize.SymmetricLevels(bits));

    [Fact]
    public void Decode_IsExactlyQTimesRangeOverLevelsInFloat32()
    {
        // The client baselines on its own decode, so the arithmetic must match bit for bit.
        const float range = 1.5f;
        for (uint u = 0; u <= 1022 * 2 / 2 * 2; u += 37)
        {
            float expected = (float)((int)u - 1023) * (range / (float)1023);
            Assert.Equal(expected, Quantize.DequantizeSymmetric(u, 11, range));
        }
    }

    [Theory]
    [InlineData(8, 0.25f, 0u, 0xBE800000u)]
    [InlineData(8, 0.25f, 127u, 0x00000000u)]
    [InlineData(8, 0.25f, 128u, 0x3B010204u)]
    [InlineData(8, 0.25f, 254u, 0x3E800000u)]
    [InlineData(11, 1.5f, 0u, 0xBFC00000u)]
    [InlineData(11, 1.5f, 1023u, 0x00000000u)]
    [InlineData(11, 1.5f, 2046u, 0x3FC00000u)]
    public void Decode_MatchesPinnedFloat32BitPatterns(int bits, float range, uint code, uint expectedBits)
    {
        // Expected bits: float32(float32(q) * float32(R / L)), as computed independently in Python.
        Assert.Equal(expectedBits, BitConverter.SingleToUInt32Bits(Quantize.DequantizeSymmetric(code, bits, range)));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(14)]
    [InlineData(16)]
    public void AllOnesCode_IsAProtocolError(int bits)
    {
        uint allOnes = (1u << bits) - 1u;
        Assert.Throws<ProtocolException>(() => Quantize.DequantizeSymmetric(allOnes, bits, 1f));
    }

    [Fact]
    public void RoundTrip_ErrorIsAtMostHalfAStep()
    {
        const float range = 0.25f;
        float step = range / 127f;
        for (float v = -range; v <= range; v += 0.0007f)
        {
            float back = Quantize.DequantizeSymmetric(EncodingHelpers.QuantizeSymmetric(v, 8, range), 8, range);
            Assert.True(MathF.Abs(back - v) <= (step / 2f) + 1e-6f, $"v={v} back={back}");
        }
    }

    [Fact]
    public void DeltaPosition_StillComponentsDecodeToExactlyZero()
    {
        byte[] bytes = EncodingHelpers.PackDeltaPosition(new Vector3(0f, 0.2f, 0f), 1.5f);

        Vector3 v = Quantize.DecodeDeltaPosition(bytes, 1.5f);

        Assert.Equal(0f, v.X);
        Assert.Equal(0f, v.Z);
        Assert.NotEqual(0f, v.Y);
    }

    [Fact]
    public void DeltaRotation_ZeroDecodesToIdentity()
    {
        Quaternion q = Quantize.DecodeQuatDropW(EncodingHelpers.PackQuatDropW(Quaternion.Identity, 0.25f), 0.25f);

        Assert.Equal(Quaternion.Identity, q);
    }

    [Fact]
    public void Message_WithAllOnesCode_IsRejectedBeforeAnythingIsEmitted()
    {
        // A TruePosition whose X code is 0xFFFF, after an otherwise valid Show.
        byte[] msg = new WireBuilder().BeginSession().Show(3)
            .Raw(EncodingHelpers.Envelope(EventType.TruePosition, 1))
            .Raw(0xFF, 0xFF, 0x00, 0x80, 0x00, 0x80)
            .Build();
        var events = new List<string>();

        Assert.Throws<ProtocolException>(() => new WireDecoder().DecodeMessage(msg, new CountingSink(events)));
        Assert.Empty(events);
    }

    [Fact]
    public void DeltaScaleAndAxisAngle_AllOnesCodes_AreRejected()
    {
        byte[] scale = new WireBuilder().BeginSession().Raw(EncodingHelpers.Envelope(EventType.DeltaUniformScale, 1)).Raw(0xFF).Build();
        Assert.Throws<ProtocolException>(() => new WireDecoder().DecodeMessage(scale, new CountingSink([])));

        var axis = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(axis, (ushort)((0x3FFF << 2) | 1));
        byte[] angle = new WireBuilder().BeginSession().Raw(EncodingHelpers.Envelope(EventType.DeltaRotationSingleAxis, 1)).Raw(axis).Build();
        Assert.Throws<ProtocolException>(() => new WireDecoder().DecodeMessage(angle, new CountingSink([])));
    }

    [Fact]
    public void WireRanges_DefaultsAndStepsFollowTheContract()
    {
        WireRanges r = WireRanges.Default;

        Assert.Equal(0.25f, r.DeltaRotationComponentRange);
        Assert.Equal(1.5f / 511f, r.DeltaPositionStep);
        Assert.Equal(0.25f / 127f, r.DeltaRotationComponentStep);
        Assert.Equal(1f / 127f, r.DeltaScaleStep);
    }

    private sealed class CountingSink(List<string> events) : WireEventSinkBase
    {
        public override void OnShow(ushort objectId) => events.Add("show");
        public override void OnTransform(ushort objectId, in TransformUpdate update) => events.Add("transform");
    }
}
