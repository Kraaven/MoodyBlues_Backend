namespace MoodyBlues.Backend.Session;

/// <summary>
/// Optimizes the un-optimized <see cref="RawObjectTimeline"/> list AnimationBuilder produced into
/// the final <see cref="AnimationFileDto"/> that gets gzipped to disk. Three stages per channel,
/// in order (see Spec discussion -- ACL was considered and rejected: it targets skeletal bone
/// hierarchies, not independent per-object transforms, and would add a native dependency for no
/// real benefit here):
///   1. Key reduction (Douglas-Peucker) -- drop points that don't measurably change the curve.
///   2. Constant/linear elimination -- collapse trivial channels to a single value or a 2-point line.
///   3. Range-reduction quantization -- remaining keyframes are stored as 16-bit values over the
///      channel's own [min, max], halving their size vs. a 4-byte float.
/// GZip compression of the resulting JSON happens one level up, in SessionFinalizer.
/// </summary>
public static class AnimationOptimizer
{
    public static AnimationFileDto Optimize(double durationSeconds, List<RawObjectTimeline> rawObjects)
{
    var objects = new AnimationObjectDto[rawObjects.Count];
    Parallel.For(0, rawObjects.Count, i => { objects[i] = OptimizeObject(rawObjects[i]); });
    return new AnimationFileDto { DurationSeconds = durationSeconds, Objects = objects.ToList() };
}

    /// <summary>Total keyframe count across every channel of every object, for Session.KeyframeCount.</summary>
    public static long CountKeyframes(AnimationFileDto file)
    {
        long total = 0;
        foreach (var obj in file.Objects)
        {
            foreach (var channel in obj.Channels.Values)
            {
                total += channel.Type switch
                {
                    "constant" => 1,
                    "linear" => 2,
                    "keyframes" => channel.Values?.Length ?? 0,
                    _ => 0,
                };
            }
        }

        return total;
    }

    private static AnimationObjectDto OptimizeObject(RawObjectTimeline raw)
    {
        OptimizeRotationChannels(raw, out AnimationChannelDto rotX, out AnimationChannelDto rotY, out AnimationChannelDto rotZ, out AnimationChannelDto rotW);

        var channels = new Dictionary<string, AnimationChannelDto>
        {
            ["posX"] = OptimizeChannel(raw.PosX),
            ["posY"] = OptimizeChannel(raw.PosY),
            ["posZ"] = OptimizeChannel(raw.PosZ),
            ["rotX"] = rotX,
            ["rotY"] = rotY,
            ["rotZ"] = rotZ,
            ["rotW"] = rotW,
            ["sclX"] = OptimizeChannel(raw.SclX),
            ["sclY"] = OptimizeChannel(raw.SclY),
            ["sclZ"] = OptimizeChannel(raw.SclZ),
        };

        var lifecycle = raw.Lifecycle
            .Select(l => new AnimationLifecycleDto { Time = l.Time, Type = l.Type })
            .ToList();

        return new AnimationObjectDto
        {
            ObjectId = raw.ObjectId,
            InstanceIndex = raw.InstanceIndex,
            TemplateObjectId = raw.TemplateObjectId,
            ParentObjectId = raw.ParentObjectId,
            Channels = channels,
            Lifecycle = lifecycle,
        };
    }

    private static void OptimizeRotationChannels(
        RawObjectTimeline raw,
        out AnimationChannelDto rotX,
        out AnimationChannelDto rotY,
        out AnimationChannelDto rotZ,
        out AnimationChannelDto rotW)
    {
        int n = raw.RotX.Count;
        if (n == 0)
        {
            rotX = rotY = rotZ = rotW = new AnimationChannelDto { Type = "constant", Value = 0f };
            return;
        }

        if (n == 1)
        {
            rotX = new AnimationChannelDto { Type = "constant", Value = raw.RotX[0].Value };
            rotY = new AnimationChannelDto { Type = "constant", Value = raw.RotY[0].Value };
            rotZ = new AnimationChannelDto { Type = "constant", Value = raw.RotZ[0].Value };
            rotW = new AnimationChannelDto { Type = "constant", Value = raw.RotW[0].Value };
            return;
        }

        float min = float.MaxValue, max = float.MinValue;
        foreach (var channel in new[] { raw.RotX, raw.RotY, raw.RotZ, raw.RotW })
        {
            foreach (var (_, value) in channel)
            {
                if (value < min) min = value;
                if (value > max) max = value;
            }
        }

        if (max - min < 1e-5f)
        {
            rotX = new AnimationChannelDto { Type = "constant", Value = raw.RotX[0].Value };
            rotY = new AnimationChannelDto { Type = "constant", Value = raw.RotY[0].Value };
            rotZ = new AnimationChannelDto { Type = "constant", Value = raw.RotZ[0].Value };
            rotW = new AnimationChannelDto { Type = "constant", Value = raw.RotW[0].Value };
            return;
        }

        double tolerance = Math.Max(1e-5, (max - min) * 0.002);
        bool[] keep = DouglasPeucker.GetKeepMaskMulti(
            [raw.RotX, raw.RotY, raw.RotZ, raw.RotW],
            tolerance);

        var simplifiedX = new List<(double Time, float Value)>();
        var simplifiedY = new List<(double Time, float Value)>();
        var simplifiedZ = new List<(double Time, float Value)>();
        var simplifiedW = new List<(double Time, float Value)>();
        for (int i = 0; i < n; i++)
        {
            if (!keep[i]) continue;
            simplifiedX.Add(raw.RotX[i]);
            simplifiedY.Add(raw.RotY[i]);
            simplifiedZ.Add(raw.RotZ[i]);
            simplifiedW.Add(raw.RotW[i]);
        }

        if (simplifiedX.Count <= 2)
        {
            rotX = new AnimationChannelDto { Type = "linear", T0 = simplifiedX[0].Time, V0 = simplifiedX[0].Value, T1 = simplifiedX[^1].Time, V1 = simplifiedX[^1].Value };
            rotY = new AnimationChannelDto { Type = "linear", T0 = simplifiedY[0].Time, V0 = simplifiedY[0].Value, T1 = simplifiedY[^1].Time, V1 = simplifiedY[^1].Value };
            rotZ = new AnimationChannelDto { Type = "linear", T0 = simplifiedZ[0].Time, V0 = simplifiedZ[0].Value, T1 = simplifiedZ[^1].Time, V1 = simplifiedZ[^1].Value };
            rotW = new AnimationChannelDto { Type = "linear", T0 = simplifiedW[0].Time, V0 = simplifiedW[0].Value, T1 = simplifiedW[^1].Time, V1 = simplifiedW[^1].Value };
            return;
        }

        float qMin = simplifiedX[0].Value, qMax = simplifiedX[0].Value;
        foreach (var list in new[] { simplifiedX, simplifiedY, simplifiedZ, simplifiedW })
        {
            foreach (var (_, value) in list)
            {
                if (value < qMin) qMin = value;
                if (value > qMax) qMax = value;
            }
        }

        if (qMax - qMin < 1e-6f) qMax = qMin + 1e-6f;

        int count = simplifiedX.Count;
        var times = new double[count];
        var valuesX = new ushort[count];
        var valuesY = new ushort[count];
        var valuesZ = new ushort[count];
        var valuesW = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            times[i] = simplifiedX[i].Time;
            valuesX[i] = QuantizeToUshort(simplifiedX[i].Value, qMin, qMax);
            valuesY[i] = QuantizeToUshort(simplifiedY[i].Value, qMin, qMax);
            valuesZ[i] = QuantizeToUshort(simplifiedZ[i].Value, qMin, qMax);
            valuesW[i] = QuantizeToUshort(simplifiedW[i].Value, qMin, qMax);
        }

        rotX = new AnimationChannelDto { Type = "keyframes", Min = qMin, Max = qMax, Times = times, Values = valuesX };
        rotY = new AnimationChannelDto { Type = "keyframes", Min = qMin, Max = qMax, Times = times, Values = valuesY };
        rotZ = new AnimationChannelDto { Type = "keyframes", Min = qMin, Max = qMax, Times = times, Values = valuesZ };
        rotW = new AnimationChannelDto { Type = "keyframes", Min = qMin, Max = qMax, Times = times, Values = valuesW };
    }

    private static ushort QuantizeToUshort(float value, float min, float max)
    {
        float t = (value - min) / (max - min);
        t = Math.Clamp(t, 0f, 1f);
        return (ushort)Math.Round(t * 65535f);
    }

    private static AnimationChannelDto OptimizeChannel(List<(double Time, float Value)> points)
    {
        if (points.Count == 0)
        {
            return new AnimationChannelDto { Type = "constant", Value = 0f };
        }

        if (points.Count == 1)
        {
            return new AnimationChannelDto { Type = "constant", Value = points[0].Value };
        }

        float min = points[0].Value, max = points[0].Value;
        foreach (var (_, value) in points)
        {
            if (value < min) min = value;
            if (value > max) max = value;
        }

        // Stage 3 (constant elimination): every value is the same within a tight epsilon.
        if (max - min < 1e-5f)
        {
            return new AnimationChannelDto { Type = "constant", Value = points[0].Value };
        }

        // Stage 1: Douglas-Peucker key reduction. Tolerance scales with the channel's own value
        // range so e.g. a +/-500-unit position channel and a +/-1 quaternion component channel
        // both get a sensible, visually-negligible error bound.
        double tolerance = Math.Max(1e-5, (max - min) * 0.002);
        List<(double Time, float Value)> simplified = DouglasPeucker.Simplify(points, tolerance);

        // Stage 2: if key reduction collapsed everything to just the two endpoints, that's a
        // straight line -- store it as one instead of a 2-entry keyframe array.
        if (simplified.Count <= 2)
        {
            (double t0, float v0) = simplified[0];
            (double t1, float v1) = simplified[^1];
            return new AnimationChannelDto { Type = "linear", T0 = t0, V0 = v0, T1 = t1, V1 = v1 };
        }

        // Stage 3: 16-bit quantization over the *simplified* curve's own range (tighter than the
        // original range, since DP may have dropped the original extremes' neighbors).
        float qMin = simplified[0].Value, qMax = simplified[0].Value;
        foreach (var (_, value) in simplified)
        {
            if (value < qMin) qMin = value;
            if (value > qMax) qMax = value;
        }

        if (qMax - qMin < 1e-6f)
        {
            qMax = qMin + 1e-6f;
        }

        var times = new double[simplified.Count];
        var values = new ushort[simplified.Count];
        for (int i = 0; i < simplified.Count; i++)
        {
            times[i] = simplified[i].Time;
            float t = (simplified[i].Value - qMin) / (qMax - qMin);
            t = Math.Clamp(t, 0f, 1f);
            values[i] = (ushort)Math.Round(t * 65535f);
        }

        return new AnimationChannelDto { Type = "keyframes", Min = qMin, Max = qMax, Times = times, Values = values };
    }
}
