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
        var objects = rawObjects.Select(OptimizeObject).ToList();
        return new AnimationFileDto { DurationSeconds = durationSeconds, Objects = objects };
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
        var channels = new Dictionary<string, AnimationChannelDto>
        {
            ["posX"] = OptimizeChannel(raw.PosX),
            ["posY"] = OptimizeChannel(raw.PosY),
            ["posZ"] = OptimizeChannel(raw.PosZ),
            ["rotX"] = OptimizeChannel(raw.RotX),
            ["rotY"] = OptimizeChannel(raw.RotY),
            ["rotZ"] = OptimizeChannel(raw.RotZ),
            ["rotW"] = OptimizeChannel(raw.RotW),
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
            TemplateObjectId = raw.TemplateObjectId,
            ParentObjectId = raw.ParentObjectId,
            Channels = channels,
            Lifecycle = lifecycle,
        };
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
