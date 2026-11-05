using MoodyBlues.Backend.Protocol;

namespace MoodyBlues.Backend.Session;

/// <summary>Un-optimized per-channel (time, value) samples for one tracked object, plus its lifecycle timeline.</summary>
public sealed class RawObjectTimeline
{
    public required ushort ObjectId { get; init; }

    public int InstanceIndex { get; init; }

    /// <summary>Set only for runtime-spawned objects (see InstantiateObjectEvent) -- null for objects present in the initial scene walk.</summary>
    public ushort? TemplateObjectId { get; init; }

    /// <summary>Set only for runtime-spawned objects (see InstantiateObjectEvent) -- null for objects present in the initial scene walk.</summary>
    public ushort? ParentObjectId { get; init; }

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
    private readonly record struct ObjectInstanceKey(ushort ObjectId, int InstanceIndex);

    public static List<RawObjectTimeline> Build(SessionTimeline timeline)
    {
        var byKey = new Dictionary<ObjectInstanceKey, RawObjectTimeline>();
        var instanceIndexByObject = new Dictionary<ushort, int>();
        var deletedByObject = new Dictionary<ushort, bool>();

        foreach (ushort objectId in timeline.ObjectOrder)
        {
            instanceIndexByObject.TryAdd(objectId, 0);
            deletedByObject.TryAdd(objectId, false);
        }

        foreach (TimelineSample sample in timeline.Samples)
        {
            if (sample.Lifecycle == LifecycleEventType.Spawn && deletedByObject.GetValueOrDefault(sample.ObjectId))
            {
                instanceIndexByObject[sample.ObjectId] = instanceIndexByObject.GetValueOrDefault(sample.ObjectId) + 1;
                deletedByObject[sample.ObjectId] = false;
            }

            if (sample.Lifecycle == LifecycleEventType.Delete)
            {
                deletedByObject[sample.ObjectId] = true;
            }

            int instanceIndex = instanceIndexByObject.GetValueOrDefault(sample.ObjectId, 0);
            var key = new ObjectInstanceKey(sample.ObjectId, instanceIndex);

            if (!byKey.TryGetValue(key, out RawObjectTimeline? obj))
            {
                obj = new RawObjectTimeline
                {
                    ObjectId = sample.ObjectId,
                    InstanceIndex = instanceIndex,
                    TemplateObjectId = sample.Lifecycle == LifecycleEventType.Spawn ? sample.TemplateObjectId : null,
                    ParentObjectId = sample.Lifecycle == LifecycleEventType.Spawn ? sample.ParentObjectId : null,
                };
                byKey[key] = obj;
            }

            AppendPoint(obj.PosX, sample.Time, sample.Position.X);
            AppendPoint(obj.PosY, sample.Time, sample.Position.Y);
            AppendPoint(obj.PosZ, sample.Time, sample.Position.Z);
            AppendPoint(obj.RotX, sample.Time, sample.Rotation.X);
            AppendPoint(obj.RotY, sample.Time, sample.Rotation.Y);
            AppendPoint(obj.RotZ, sample.Time, sample.Rotation.Z);
            AppendPoint(obj.RotW, sample.Time, sample.Rotation.W);
            AppendPoint(obj.SclX, sample.Time, sample.Scale.X);
            AppendPoint(obj.SclY, sample.Time, sample.Scale.Y);
            AppendPoint(obj.SclZ, sample.Time, sample.Scale.Z);

            if (sample.Lifecycle is { } lifecycle)
            {
                obj.Lifecycle.Add((sample.Time, lifecycle));
            }
        }

        return byKey.Values.ToList();
    }

    private static void AppendPoint(List<(double Time, float Value)> list, double time, float value)
    {
        int count = list.Count;
        if (count >= 2 && list[count - 1].Value == value && list[count - 2].Value == value)
        {
            list[count - 1] = (time, value);
        }
        else
        {
            list.Add((time, value));
        }
    }
}
