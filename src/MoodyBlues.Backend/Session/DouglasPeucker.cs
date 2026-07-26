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
}
