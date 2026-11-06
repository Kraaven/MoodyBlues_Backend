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
    public static readonly WireRanges Default = new(500f, -1f, 10f, 1.5f, 0.5f, 2f, 1f);

    /// <summary>Quantization step of the coarsest delta-position axis (Y, 10 bits).</summary>
    public float DeltaPositionStep => 2f * DeltaPositionRange / 1023f;

    /// <summary>Quantization step of one drop-W delta-rotation component (8 bits).</summary>
    public float DeltaRotationComponentStep => 2f * DeltaRotationComponentRange / 255f;

    /// <summary>Quantization step of one delta-scale component (8 bits).</summary>
    public float DeltaScaleStep => 2f * DeltaScaleRange / 255f;

    public bool IsValid =>
        float.IsFinite(PositionRange) && PositionRange > 0 &&
        float.IsFinite(ScaleMin) && float.IsFinite(ScaleMax) && ScaleMax > ScaleMin &&
        float.IsFinite(DeltaPositionRange) && DeltaPositionRange > 0 &&
        float.IsFinite(DeltaRotationComponentRange) && DeltaRotationComponentRange > 0 &&
        float.IsFinite(DeltaSingleAxisAngleRange) && DeltaSingleAxisAngleRange > 0 &&
        float.IsFinite(DeltaScaleRange) && DeltaScaleRange > 0;
}
