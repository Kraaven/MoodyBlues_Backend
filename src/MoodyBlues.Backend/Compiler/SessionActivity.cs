using System.Text.Json;

namespace MoodyBlues.Backend.Compiler;

/// <summary>
/// The shape of a session as a miniature timeline: where the motion is, and where the errors,
/// warnings, spawns and gaps fall. Every position is a fraction of the session, 0 to 1. Computed
/// by the compiler (so the dashboard needs no replay download to draw a session's glyph) and
/// stored as JSON on the session; the same object is what the API returns as <c>activity</c>.
/// </summary>
/// <param name="Bins">Keyframe activity per equal time bucket, <see cref="BinCount"/> numbers, each 0 to 1.</param>
/// <param name="Errors">Session fractions of the Error logs.</param>
/// <param name="Warnings">Session fractions of the Warning logs.</param>
/// <param name="Spawns">Session fractions of the Spawn events.</param>
/// <param name="Gaps">[start, end] fractions of the spans with no data (disconnects, data gaps).</param>
public sealed record SessionActivity(
    IReadOnlyList<double> Bins,
    IReadOnlyList<double> Errors,
    IReadOnlyList<double> Warnings,
    IReadOnlyList<double> Spawns,
    IReadOnlyList<double[]> Gaps)
{
    public const int BinCount = 48;

    /// <summary>A data gap is a moment, not a span; it is given this width so it can be seen.</summary>
    private const double DataGapWidth = 0.006;

    /// <summary>A disconnect this close to the end is the session's normal end, not a gap.</summary>
    private const double EndTolerance = 0.05;

    private const byte WarningLevel = 2;
    private const byte ErrorLevel = 3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Counts the fitted keyframes (what the replay file contains) per time bucket and places the
    /// logs and lifecycle events as session fractions. Values are rounded to 4 decimals to keep the JSON small.
    /// </summary>
    public static SessionActivity Compute(CompiledReplay replay)
    {
        double total = replay.DurationSeconds;
        if (!(total > 0))
        {
            return new SessionActivity(new double[BinCount], [], [], [], []);
        }

        double Fraction(double seconds) => Math.Clamp(seconds / total, 0, 1);

        var counts = new int[BinCount];
        foreach (ReplayObject o in replay.Objects)
        {
            foreach (ChannelFitter? track in new[] { o.Position, o.Rotation, o.Scale })
            {
                if (track is null)
                {
                    continue;
                }

                foreach (double time in track.KeyTimes)
                {
                    counts[Math.Min(BinCount - 1, (int)Math.Floor(Fraction(time) * BinCount))]++;
                }
            }
        }

        // The square root keeps a few very busy moments from flattening everything else.
        int peak = Math.Max(1, counts.Max());
        double[] bins = counts.Select(c => Round(Math.Sqrt((double)c / peak))).ToArray();

        var errors = new List<double>();
        var warnings = new List<double>();
        foreach (ReplayLog log in replay.Logs)
        {
            if (log.Level == ErrorLevel)
            {
                errors.Add(Round(Fraction(log.Time)));
            }
            else if (log.Level == WarningLevel)
            {
                warnings.Add(Round(Fraction(log.Time)));
            }
        }

        var spawns = new List<double>();
        var gaps = new List<double[]>();
        double? down = null;
        foreach (ReplayEvent e in replay.Events.OrderBy(e => e.Time))
        {
            switch (e.Type)
            {
                case ReplayEventType.Spawn:
                    spawns.Add(Round(Fraction(e.Time)));
                    break;
                case ReplayEventType.DataGap:
                    gaps.Add([Round(Fraction(e.Time)), Round(Math.Min(1, Fraction(e.Time) + DataGapWidth))]);
                    break;
                case ReplayEventType.Disconnected when e.Time < total - EndTolerance:
                    down = e.Time;
                    break;
                case ReplayEventType.Connected when down is not null:
                    gaps.Add([Round(Fraction(down.Value)), Round(Fraction(e.Time))]);
                    down = null;
                    break;
            }
        }

        return new SessionActivity(bins, errors, warnings, spawns, gaps);
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The stored activity, or null when there is none (the session is not Ready) or it cannot be read.</summary>
    public static SessionActivity? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SessionActivity>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double Round(double value) => Math.Round(value, 4);
}
