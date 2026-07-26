using MoodyBlues.Backend.Protocol;

namespace MoodyBlues.Backend.Session;

/// <summary>Un-optimized per-channel (time, value) samples for one tracked object, plus its lifecycle timeline.</summary>
public sealed class RawObjectTimeline
{
    public required ushort ObjectId { get; init; }

    /// <summary>Set only for runtime-spawned objects (see InstantiateObjectEvent) -- null for objects present in the initial scene walk.</summary>
    public ushort? TemplateObjectId { get; init; }

    public List<(double Time, float Value)> PosX { get; } = new();
    public List<(double Time, float Value)> PosY { get; } = new();
    public List<(double Time, float Value)> PosZ { get; } = new();
    public List<(double Time, float Value)> RotX { get; } = new();
    public List<(double Time, float Value)> RotY { get; } = new();
    public List<(double Time, float Value)> RotZ { get; } = new();
    public List<(double Time, float Value)> RotW { get; } = new();
    public List<(double Time, float Value)> SclX { get; } = new();
    public List<(double Time, float Value)> SclY { get; } = new();
    public List<(double Time, float Value)> SclZ { get; } = new();

    public List<(double Time, LifecycleEventType Type)> Lifecycle { get; } = new();
}

/// <summary>
/// Turns a finalized <see cref="SessionTimeline"/> (raw per-event snapshots, in wall-clock order)
/// into one <see cref="RawObjectTimeline"/> per tracked object -- still un-optimized (every
/// snapshot becomes a channel point); <c>AnimationOptimizer</c> does the size reduction.
/// </summary>
public static class AnimationBuilder
{
    public static List<RawObjectTimeline> Build(SessionTimeline timeline)
    {
        var byObjectId = new Dictionary<ushort, RawObjectTimeline>();

        foreach (ushort objectId in timeline.ObjectOrder)
        {
            byObjectId[objectId] = new RawObjectTimeline
            {
                ObjectId = objectId,
                TemplateObjectId = timeline.TemplateObjectIdByObjectId.TryGetValue(objectId, out ushort templateId)
                    ? templateId
                    : null,
            };
        }

        foreach (TimelineSample sample in timeline.Samples)
        {
            if (!byObjectId.TryGetValue(sample.ObjectId, out RawObjectTimeline? obj))
            {
                // Shouldn't happen (ObjectOrder is populated from the exact same events), but
                // don't let one inconsistent sample take down the whole build.
                continue;
            }

            obj.PosX.Add((sample.Time, sample.Position.X));
            obj.PosY.Add((sample.Time, sample.Position.Y));
            obj.PosZ.Add((sample.Time, sample.Position.Z));
            obj.RotX.Add((sample.Time, sample.Rotation.X));
            obj.RotY.Add((sample.Time, sample.Rotation.Y));
            obj.RotZ.Add((sample.Time, sample.Rotation.Z));
            obj.RotW.Add((sample.Time, sample.Rotation.W));
            obj.SclX.Add((sample.Time, sample.Scale.X));
            obj.SclY.Add((sample.Time, sample.Scale.Y));
            obj.SclZ.Add((sample.Time, sample.Scale.Z));

            if (sample.Lifecycle is { } lifecycle)
            {
                obj.Lifecycle.Add((sample.Time, lifecycle));
            }
        }

        return byObjectId.Values.ToList();
    }
}
