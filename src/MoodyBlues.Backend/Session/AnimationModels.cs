using System.Text.Json.Serialization;
using MoodyBlues.Backend.Protocol;

namespace MoodyBlues.Backend.Session;

/// <summary>One channel's optimized representation -- exactly one of the three shapes below, per <see cref="Type"/>.</summary>
public sealed class AnimationChannelDto
{
    /// <summary>"constant" | "linear" | "keyframes".</summary>
    public required string Type { get; init; }

    /// <summary>Only set when Type == "constant".</summary>
    public float? Value { get; init; }

    /// <summary>Only set when Type == "linear" (start point).</summary>
    public double? T0 { get; init; }

    public float? V0 { get; init; }

    /// <summary>Only set when Type == "linear" (end point).</summary>
    public double? T1 { get; init; }

    public float? V1 { get; init; }

    /// <summary>Only set when Type == "keyframes" -- dequantize with Min/Max: value = Min + (raw/65535)*(Max-Min).</summary>
    public float? Min { get; init; }

    public float? Max { get; init; }

    public double[]? Times { get; init; }

    public ushort[]? Values { get; init; }
}

public sealed class AnimationLifecycleDto
{
    public required double Time { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required LifecycleEventType Type { get; init; }
}

public sealed class AnimationObjectDto
{
    public required ushort ObjectId { get; init; }

    /// <summary>Zero for the first lifecycle segment; increments when the same wire ObjectId is reused after Delete.</summary>
    public int InstanceIndex { get; init; }

    /// <summary>Null for objects present in the initial scene walk; set for runtime-spawned instances (see InstantiateObjectEvent).</summary>
    public ushort? TemplateObjectId { get; init; }

    /// <summary>Null for scene objects; parent ObjectID for runtime-spawned instances (0 = scene root).</summary>
    public ushort? ParentObjectId { get; init; }

    public required Dictionary<string, AnimationChannelDto> Channels { get; init; }

    public required List<AnimationLifecycleDto> Lifecycle { get; init; }
}

/// <summary>The full on-disk (gzipped) animation document for one Session.</summary>
public sealed class AnimationFileDto
{
    /// <summary>Schema version for forward-compatible parsing on the frontend.</summary>
    public int SchemaVersion { get; init; } = 1;

    public double DurationSeconds { get; init; }

    public required List<AnimationObjectDto> Objects { get; init; }
}
