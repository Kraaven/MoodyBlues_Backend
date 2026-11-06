namespace MoodyBlues.Backend.Compiler;

/// <summary>
/// Fits one animated property of one object (position, rotation or scale) with a piecewise
/// cubic Hermite curve: keys of <c>(time, value, inTangent, outTangent)</c>, where tangents are
/// value-per-second slopes and may differ on either side of a key ("broken" tangents). This is
/// the same curve model as Unity's AnimationCurve, and maps directly onto three.js's
/// <c>InterpolateBezier</c> with control points one third of the way along each interval.
///
/// <para><b>Error guarantee.</b> Every input sample is reproduced within <see cref="Tolerance"/>:
/// Euclidean distance for vectors, angle in radians for rotations (evaluated after
/// normalization, as the viewer does). The check runs against every sample, after fitting.</para>
///
/// <para><b>Algorithm.</b> Greedy and streaming. From the last committed key, the segment end is
/// pushed as far forward as the tolerance allows, found by doubling then binary search. For each
/// candidate end, the two endpoint values are fixed (they are samples) and the two tangents are
/// solved by linear least squares over the samples in between. Constant and linear motion fall
/// out as the special cases where the solved tangents are zero or equal to the slope. Only a
/// bounded window of samples is held, so memory does not grow with session length.</para>
///
/// <para><b>Hold keys.</b> The client sends nothing while a property is unchanged. When a
/// sample arrives after a gap of more than 1.5 capture ticks, a key holding the previous value
/// is inserted one tick earlier, so the curve stays flat during the still period instead of
/// drifting across it. A <c>discontinuity</c> sample (a reparent re-baseline) gets its hold key
/// <see cref="StepEpsilon"/> before it instead, so the jump happens almost instantly.</para>
/// </summary>
public sealed class ChannelFitter
{
    /// <summary>Spacing used to keep key times strictly increasing and to place step hold keys.</summary>
    public const double StepEpsilon = 1e-3;

    private const int MaxWindow = 512;

    private readonly int _dim;
    private readonly bool _isRotation;
    private readonly double _tick;

    // Pending samples. Index 0 is always the last committed key (once one exists).
    private readonly List<double> _t = new(MaxWindow + 4);
    private readonly List<float> _v = new((MaxWindow + 4) * 4);
    private bool _firstKeyCommitted;

    // Scratch for tangent solving and evaluation.
    private readonly float[] _m0;
    private readonly float[] _m1;
    private readonly float[] _bestM0;
    private readonly float[] _bestM1;
    private readonly float[] _eval;
    private readonly float[] _eval2;
    private readonly float[] _mid;
    private readonly float[] _ref;
    private readonly float[] _last;

    public ChannelFitter(int dimension, bool isRotation, float tolerance, double tickSeconds)
    {
        if (dimension is not (3 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(dimension));
        }

        _dim = dimension;
        _isRotation = isRotation;
        Tolerance = tolerance;
        _tick = tickSeconds > 0 ? tickSeconds : 1.0 / 40.0;
        _m0 = new float[dimension];
        _m1 = new float[dimension];
        _bestM0 = new float[dimension];
        _bestM1 = new float[dimension];
        _eval = new float[dimension];
        _eval2 = new float[dimension];
        _mid = new float[dimension];
        _ref = new float[dimension];
        _last = new float[dimension];
    }

    public float Tolerance { get; }

    public int Dimension => _dim;

    // Output keys, flattened by dimension.
    public List<double> KeyTimes { get; } = [];

    public List<float> KeyValues { get; } = [];

    public List<float> KeyInTangents { get; } = [];

    public List<float> KeyOutTangents { get; } = [];

    public int KeyCount => KeyTimes.Count;

    public bool HasSamples => _t.Count > 0;

    public double LastSampleTime => _t.Count > 0 ? _t[^1] : double.NegativeInfinity;

    /// <summary>Adds a sample. Values are copied. Rotations must be unit quaternions (x, y, z, w).</summary>
    public void Add(double time, ReadOnlySpan<float> value, bool discontinuity = false)
    {
        Span<float> v = stackalloc float[_dim];
        value[.._dim].CopyTo(v);

        if (_t.Count > 0)
        {
            double lastT = _t[^1];
            CopyLast(_last);

            if (_isRotation && Dot(v, _last) < 0f)
            {
                for (int i = 0; i < _dim; i++)
                {
                    v[i] = -v[i];
                }
            }

            double gap = time - lastT;
            double holdAt = discontinuity ? time - StepEpsilon : time - _tick;
            bool needsHold = discontinuity ? gap > 2 * StepEpsilon : gap > 1.5 * _tick;
            if (needsHold && holdAt > lastT + StepEpsilon * 0.5 && !Equal(_last, v))
            {
                Append(holdAt, _last);
            }

            if (time <= _t[^1])
            {
                // Same tick (or clock went backwards): keep times strictly increasing.
                time = _t[^1] + StepEpsilon;
            }
        }

        Append(time, v);

        if (_t.Count >= MaxWindow)
        {
            Fit(final: false);
        }
    }

    /// <summary>Fits everything still pending. Call once, after the last sample.</summary>
    public void Flush()
    {
        Fit(final: true);
        if (KeyCount > 0)
        {
            // The last key's out-tangent is never used for interpolation; mirror the in-tangent.
            int o = (KeyCount - 1) * _dim;
            for (int i = 0; i < _dim; i++)
            {
                KeyOutTangents[o + i] = KeyInTangents[o + i];
            }
        }
    }

    private void Append(double time, ReadOnlySpan<float> v)
    {
        _t.Add(time);
        for (int i = 0; i < _dim; i++)
        {
            _v.Add(v[i]);
        }
    }

    private void CopyLast(Span<float> dest)
    {
        int o = (_t.Count - 1) * _dim;
        for (int i = 0; i < _dim; i++)
        {
            dest[i] = _v[o + i];
        }
    }

    private void Fit(bool final)
    {
        int n = _t.Count;
        if (n == 0)
        {
            return;
        }

        if (!_firstKeyCommitted)
        {
            AddKey(0, inTangent: null);
            _firstKeyCommitted = true;
        }

        bool forced = n >= MaxWindow;
        int s = 0;
        while (s < n - 1)
        {
            int e = FindSegmentEnd(s, n - 1);
            if (!final && !forced && e == n - 1)
            {
                break; // the segment may extend further once more samples arrive
            }

            // Recompute the tangents for the chosen end (FindSegmentEnd probes many ends).
            Fits(s, e);
            int prevKey = KeyCount - 1;
            for (int i = 0; i < _dim; i++)
            {
                KeyOutTangents[prevKey * _dim + i] = _bestM0[i];
            }

            AddKey(e, _bestM1);
            s = e;
        }

        // Drop committed samples, keeping index s (the latest key) as the new window start.
        if (s > 0)
        {
            _t.RemoveRange(0, s);
            _v.RemoveRange(0, s * _dim);
        }
    }

    private void AddKey(int sampleIndex, float[]? inTangent)
    {
        KeyTimes.Add(_t[sampleIndex]);
        for (int i = 0; i < _dim; i++)
        {
            KeyValues.Add(_v[sampleIndex * _dim + i]);
            KeyInTangents.Add(inTangent?[i] ?? 0f);
            KeyOutTangents.Add(0f);
        }
    }

    /// <summary>Furthest end index in (s, max] whose segment from s fits within tolerance.</summary>
    private int FindSegmentEnd(int s, int max)
    {
        // Segments of 2, 3 and 4 samples can be interpolated exactly (line, parabola, cubic);
        // whether they also stay sensible between samples is up to Fits. Two samples always fit.
        int good = s + 1;
        for (int candidate = s + 2; candidate <= Math.Min(s + 3, max); candidate++)
        {
            if (Fits(s, candidate))
            {
                good = candidate;
            }
        }

        if (good >= max)
        {
            return max;
        }

        int bad = -1;
        int step = 4;
        while (true)
        {
            int candidate = s + step;
            if (candidate >= max)
            {
                if (Fits(s, max))
                {
                    return max;
                }

                bad = max;
                break;
            }

            if (Fits(s, candidate))
            {
                good = candidate;
                step *= 2;
            }
            else
            {
                bad = candidate;
                break;
            }
        }

        while (bad - good > 1)
        {
            int mid = good + ((bad - good) / 2);
            if (Fits(s, mid))
            {
                good = mid;
            }
            else
            {
                bad = mid;
            }
        }

        return good;
    }

    /// <summary>
    /// Solves the tangents for the segment s..e and checks every sample in between. On success
    /// the tangents (value per second) are left in <see cref="_bestM0"/>/<see cref="_bestM1"/>.
    /// </summary>
    private bool Fits(int s, int e)
    {
        double ts = _t[s];
        double dt = _t[e] - ts;
        int so = s * _dim;
        int eo = e * _dim;

        // Normal equations for A = m0*dt and B = m1*dt; the matrix is shared by all components.
        double a11 = 0, a12 = 0, a22 = 0;
        Span<double> b1 = stackalloc double[_dim];
        Span<double> b2 = stackalloc double[_dim];
        for (int i = s + 1; i < e; i++)
        {
            double u = (_t[i] - ts) / dt;
            Basis(u, out double h00, out double h10, out double h01, out double h11);
            a11 += h10 * h10;
            a12 += h10 * h11;
            a22 += h11 * h11;
            int io = i * _dim;
            for (int c = 0; c < _dim; c++)
            {
                double r = _v[io + c] - (h00 * _v[so + c]) - (h01 * _v[eo + c]);
                b1[c] += h10 * r;
                b2[c] += h11 * r;
            }
        }

        double det = (a11 * a22) - (a12 * a12);
        bool solved = e - s >= 3 && det > 1e-12 * Math.Max(1.0, a11 * a22);
        bool quadratic = e - s == 2;
        double t1 = _t[s + 1] - ts;
        for (int c = 0; c < _dim; c++)
        {
            double A, B;
            if (solved)
            {
                A = ((a22 * b1[c]) - (a12 * b2[c])) / det;
                B = ((a11 * b2[c]) - (a12 * b1[c])) / det;
            }
            else if (quadratic)
            {
                // One sample in between: use the parabola through all three, whose end slopes
                // make the Hermite reproduce it exactly (local times 0, t1, dt).
                double v0 = _v[so + c], v1 = _v[so + _dim + c], v2 = _v[eo + c];
                double d0 = (v0 * ((-t1 - dt) / (t1 * dt))) + (v1 * (dt / (t1 * (dt - t1)))) + (v2 * (-t1 / (dt * (dt - t1))));
                double d2 = (v0 * ((dt - t1) / (t1 * dt))) + (v1 * (-dt / (t1 * (dt - t1)))) + (v2 * (((2 * dt) - t1) / (dt * (dt - t1))));
                A = d0 * dt;
                B = d2 * dt;
            }
            else
            {
                A = B = _v[eo + c] - _v[so + c]; // straight line, already scaled by dt
            }

            _m0[c] = (float)(A / dt);
            _m1[c] = (float)(B / dt);
        }

        for (int i = s + 1; i < e; i++)
        {
            double u = (_t[i] - ts) / dt;
            Basis(u, out double h00, out double h10, out double h01, out double h11);
            int io = i * _dim;
            for (int c = 0; c < _dim; c++)
            {
                _eval[c] = (float)((h00 * _v[so + c]) + (h10 * dt * _m0[c]) + (h01 * _v[eo + c]) + (h11 * dt * _m1[c]));
            }

            if (Error(_eval, io) > Tolerance)
            {
                return false;
            }
        }

        // Samples alone do not bound the curve between them: a cubic through a hold key and a
        // jump passes every sample yet swings far away in between. So also check each midpoint
        // against the straight line between its two neighbouring samples, allowing half the
        // chord so ordinary curved motion is unaffected.
        for (int i = s; i < e; i++)
        {
            double tm = ((_t[i] + _t[i + 1]) * 0.5) - ts;
            double u = tm / dt;
            Basis(u, out double h00, out double h10, out double h01, out double h11);
            int io = i * _dim;
            for (int c = 0; c < _dim; c++)
            {
                _eval[c] = (float)((h00 * _v[so + c]) + (h10 * dt * _m0[c]) + (h01 * _v[eo + c]) + (h11 * dt * _m1[c]));
                _mid[c] = (_v[io + c] + _v[io + _dim + c]) * 0.5f;
            }

            if (_isRotation)
            {
                Normalize(_mid);
            }

            float chord = Distance(io, io + _dim);
            if (ErrorAgainst(_eval, _mid) > Tolerance + (0.5f * chord))
            {
                return false;
            }
        }

        Array.Copy(_m0, _bestM0, _dim);
        Array.Copy(_m1, _bestM1, _dim);
        return true;
    }

    private float Error(float[] evaluated, int sampleOffset)
    {
        for (int c = 0; c < _dim; c++)
        {
            _ref[c] = _v[sampleOffset + c];
        }

        return ErrorAgainst(evaluated, _ref);
    }

    /// <summary>Distance between two stored samples: Euclidean, or angle for rotations.</summary>
    private float Distance(int offsetA, int offsetB)
    {
        for (int c = 0; c < _dim; c++)
        {
            _ref[c] = _v[offsetA + c];
            _eval2[c] = _v[offsetB + c];
        }

        return ErrorAgainst(_eval2, _ref);
    }

    private static void Normalize(float[] q)
    {
        float len = MathF.Sqrt((q[0] * q[0]) + (q[1] * q[1]) + (q[2] * q[2]) + (q[3] * q[3]));
        if (len > 1e-9f)
        {
            for (int c = 0; c < 4; c++)
            {
                q[c] /= len;
            }
        }
    }

    /// <summary>Error of an evaluated value against a unit reference: Euclidean, or angle in radians.</summary>
    private float ErrorAgainst(float[] evaluated, float[] reference)
    {
        if (_isRotation)
        {
            double len = 0, dot = 0;
            for (int c = 0; c < 4; c++)
            {
                len += evaluated[c] * evaluated[c];
            }

            len = Math.Sqrt(len);
            if (len < 1e-9)
            {
                return float.PositiveInfinity;
            }

            for (int c = 0; c < 4; c++)
            {
                dot += evaluated[c] / len * reference[c];
            }

            return (float)(2.0 * Math.Acos(Math.Min(1.0, Math.Abs(dot))));
        }

        double sum = 0;
        for (int c = 0; c < _dim; c++)
        {
            double d = evaluated[c] - reference[c];
            sum += d * d;
        }

        return (float)Math.Sqrt(sum);
    }

    /// <summary>Cubic Hermite basis functions at parameter u in [0, 1].</summary>
    private static void Basis(double u, out double h00, out double h10, out double h01, out double h11)
    {
        double u2 = u * u;
        double u3 = u2 * u;
        h00 = (2 * u3) - (3 * u2) + 1;
        h10 = u3 - (2 * u2) + u;
        h01 = (-2 * u3) + (3 * u2);
        h11 = u3 - u2;
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float d = 0;
        for (int i = 0; i < a.Length; i++)
        {
            d += a[i] * b[i];
        }

        return d;
    }

    private static bool Equal(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }
}
