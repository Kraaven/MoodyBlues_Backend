namespace MoodyBlues.Backend.Protocol;

/// <summary>
/// The tunable quantization ranges a connection declares in its <c>BeginSession</c> event
/// (Spec 6.1, 9). Every quantized value that follows on that connection is interpreted with these.
/// <see cref="Default"/> matches the Unity client's defaults and is only used by tests and tools;
/// the ingest path refuses any connection whose first event is not a BeginSession.
/// </summary>
public readonly record struct WireRanges(
    float PositionRange,
    float ScaleMin,
    float ScaleMax,
    float DeltaPositionRange,
    float DeltaRotationComponentRange,
    float DeltaSingleAxisAngleRange,
    float DeltaScaleRange)
{
    public static readonly WireRanges Default = new(500f, -1f, 10f, 1.5f, 0.25f, 2f, 1f);

    /// <summary>
    /// Quantization step of the coarsest delta-position axis (Y, 10 bits, L = 511); X and Z are 11
    /// bits (L = 1023). Steps are <c>R / L</c>, matching the symmetric quantizer.
    /// </summary>
    public float DeltaPositionStep => DeltaPositionRange / 511f;

    /// <summary>Quantization step of one drop-W delta-rotation component (8 bits).</summary>
    public float DeltaRotationComponentStep => DeltaRotationComponentRange / 127f;

    /// <summary>Quantization step of one delta-scale component (8 bits).</summary>
    public float DeltaScaleStep => DeltaScaleRange / 127f;

    /// <summary>
    /// Worst-case per-sample position error of the delta wire: half a step on each axis (X and Z
    /// 11 bits, Y 10 bits), as a vector length.
    /// </summary>
    public float DeltaPositionError
    {
        get
        {
            float xz = DeltaPositionRange / 1023f;
            float y = DeltaPositionRange / 511f;
            return 0.5f * MathF.Sqrt((2f * xz * xz) + (y * y));
        }
    }

    /// <summary>
    /// Worst-case per-sample rotation error of drop-W deltas, in radians: half a step on each of
    /// three components is sqrt(3)/2 * step as a vector, and the rotation angle is about twice that.
    /// </summary>
    public float DeltaRotationAngleError => MathF.Sqrt(3f) * DeltaRotationComponentStep;

    /// <summary>Worst-case per-sample scale error: half a step on each of three components.</summary>
    public float DeltaScaleError => 0.5f * MathF.Sqrt(3f) * DeltaScaleStep;

    public bool IsValid =>
        float.IsFinite(PositionRange) && PositionRange > 0 &&
        float.IsFinite(ScaleMin) && float.IsFinite(ScaleMax) && ScaleMax > ScaleMin &&
        float.IsFinite(DeltaPositionRange) && DeltaPositionRange > 0 &&
        float.IsFinite(DeltaRotationComponentRange) && DeltaRotationComponentRange > 0 &&
        float.IsFinite(DeltaSingleAxisAngleRange) && DeltaSingleAxisAngleRange > 0 &&
        float.IsFinite(DeltaScaleRange) && DeltaScaleRange > 0;
}
