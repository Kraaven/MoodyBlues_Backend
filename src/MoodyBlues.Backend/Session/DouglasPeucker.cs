namespace MoodyBlues.Backend.Session;

/// <summary>
/// Ramer-Douglas-Peucker polyline simplification for one (time, value) channel. Uses vertical
/// (value-space) deviation from the chord between two candidate endpoints rather than full 2D
/// perpendicular distance -- time and value live on very different scales (seconds vs. e.g.
/// +/-500 world units), so a true perpendicular distance would be dominated by whichever axis
/// has the larger range. Vertical deviation is also the standard choice for keyframe/curve
/// reduction (same idea Unity's own AnimationCurve key reduction and most glTF export tooling use).
/// </summary>
public static class DouglasPeucker
{
    public static List<(double Time, float Value)> Simplify(List<(double Time, float Value)> points, double epsilon)
    {
        int n = points.Count;
        if (n < 3)
        {
            return new List<(double, float)>(points);
        }

        var keep = new bool[n];
        keep[0] = true;
        keep[n - 1] = true;
        SimplifyRange(points, 0, n - 1, epsilon, keep);

        var result = new List<(double, float)>(n);
        for (int i = 0; i < n; i++)
        {
            if (keep[i])
            {
                result.Add(points[i]);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns a keep-mask for multiple channels that share identical timestamps.
    /// A point is kept if any channel exceeds <paramref name="epsilon"/> deviation from the chord.
    /// </summary>
    public static bool[] GetKeepMaskMulti(IReadOnlyList<List<(double Time, float Value)>> channels, double epsilon)
    {
        int n = channels[0].Count;
        if (n == 0)
        {
            return Array.Empty<bool>();
        }

        if (n < 3)
        {
            var all = new bool[n];
            Array.Fill(all, true);
            return all;
        }

        var keep = new bool[n];
        keep[0] = true;
        keep[n - 1] = true;
        SimplifyRangeMulti(channels, 0, n - 1, epsilon, keep);
        return keep;
    }

    private static void SimplifyRange(List<(double Time, float Value)> points, int start, int end, double epsilon, bool[] keep)
    {
        if (end <= start + 1)
        {
            return;
        }

        (double t0, float v0) = points[start];
        (double t1, float v1) = points[end];
        double span = t1 - t0;

        double maxDeviation = 0;
        int maxIndex = -1;

        for (int i = start + 1; i < end; i++)
        {
            double interpolated = span == 0 ? v0 : v0 + (v1 - v0) * ((points[i].Time - t0) / span);
            double deviation = Math.Abs(points[i].Value - interpolated);
            if (deviation > maxDeviation)
            {
                maxDeviation = deviation;
                maxIndex = i;
            }
        }

        if (maxIndex == -1 || maxDeviation <= epsilon)
        {
            return;
        }

        keep[maxIndex] = true;
        SimplifyRange(points, start, maxIndex, epsilon, keep);
        SimplifyRange(points, maxIndex, end, epsilon, keep);
    }

    private static void SimplifyRangeMulti(
        IReadOnlyList<List<(double Time, float Value)>> channels,
        int start,
        int end,
        double epsilon,
        bool[] keep)
    {
        if (end <= start + 1)
        {
            return;
        }

        double t0 = channels[0][start].Time;
        double t1 = channels[0][end].Time;
        double span = t1 - t0;

        double maxDeviation = 0;
        int maxIndex = -1;

        for (int i = start + 1; i < end; i++)
        {
            double deviation = 0;
            foreach (List<(double Time, float Value)> channel in channels)
            {
                float v0 = channel[start].Value;
                float v1 = channel[end].Value;
                float vi = channel[i].Value;
                double interpolated = span == 0 ? v0 : v0 + (v1 - v0) * ((channel[i].Time - t0) / span);
                deviation = Math.Max(deviation, Math.Abs(vi - interpolated));
            }

            if (deviation > maxDeviation)
            {
                maxDeviation = deviation;
                maxIndex = i;
            }
        }

        if (maxIndex == -1 || maxDeviation <= epsilon)
        {
            return;
        }

        keep[maxIndex] = true;
        SimplifyRangeMulti(channels, start, maxIndex, epsilon, keep);
        SimplifyRangeMulti(channels, maxIndex, end, epsilon, keep);
    }
}
