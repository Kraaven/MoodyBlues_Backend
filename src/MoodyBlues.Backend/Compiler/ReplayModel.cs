namespace MoodyBlues.Backend.Compiler;

/// <summary>Object kinds in the replay file's object table.</summary>
public enum ReplayObjectKind : byte
{
    /// <summary>Exists in the scene GLB from the start (includes prefab templates).</summary>
    Scene = 0,

    /// <summary>Created during the session by InstantiateObject; the viewer clones its template.</summary>
    Spawned = 1,
}

/// <summary>Animated properties. The numeric values are the channel ids in the replay file.</summary>
public enum ReplayChannel : byte
{
    Position = 0,
    Rotation = 1,
    Scale = 2,
}

/// <summary>Event types in the replay file's event table.</summary>
public enum ReplayEventType : byte
{
    Spawn = 1,
    Delete = 2,
    Show = 3,
    Hide = 4,
    Reparent = 5,

    /// <summary>Session marker: a client connection opened. A = connection index.</summary>
    Connected = 16,

    /// <summary>Session marker: a client connection closed. Aux = <see cref="Storage.RawLogCloseReason"/>.</summary>
    Disconnected = 17,

    /// <summary>Session marker: the client sent EndSession. Aux = <see cref="Protocol.SessionEndReason"/>.</summary>
    SessionEnded = 18,

    /// <summary>Session marker: a message failed validation and its data is missing from the replay.</summary>
    DataGap = 19,
}

public readonly record struct ReplayEvent(double Time, ReplayEventType Type, byte Aux, ushort ObjectId, ushort A, ushort B);

/// <summary>One object in the compiled replay.</summary>
public sealed class ReplayObject
{
    public required ushort ObjectId { get; init; }

    public ReplayObjectKind Kind { get; init; }

    public ushort TemplateObjectId { get; init; }

    /// <summary>Parent at the start of the object's life: registry parent, or the spawn parent.</summary>
    public ushort InitialParentObjectId { get; init; }

    /// <summary>Session time of the spawn, or -1 for scene objects.</summary>
    public double SpawnTime { get; init; } = -1;

    /// <summary>Session time the object (or an ancestor) was deleted, or -1.</summary>
    public double DeleteTime { get; set; } = -1;

    public ChannelFitter? Position { get; set; }

    public ChannelFitter? Rotation { get; set; }

    public ChannelFitter? Scale { get; set; }

    public ChannelFitter? Channel(ReplayChannel channel) => channel switch
    {
        ReplayChannel.Position => Position,
        ReplayChannel.Rotation => Rotation,
        _ => Scale,
    };
}

/// <summary>Everything the compiler produced for one session, before it is written to disk.</summary>
public sealed class CompiledReplay
{
    public double DurationSeconds { get; init; }

    public IReadOnlyList<ReplayObject> Objects { get; init; } = [];

    public IReadOnlyList<ReplayEvent> Events { get; init; } = [];

    public bool EndedCleanly { get; init; }

    public Protocol.SessionEndReason? EndReason { get; init; }

    public int ConnectionCount { get; init; }

    public long RawEventCount { get; init; }

    public uint DeltaPromotions { get; init; }

    public uint HardClamps { get; init; }

    public uint DroppedEvents { get; init; }

    /// <summary>Transform events for IDs that are neither in the scene nor spawned (e.g. spawned before the handshake).</summary>
    public long UnknownObjectEvents { get; init; }

    public int SpawnCount { get; init; }

    public long KeyframeCount => Objects.Sum(o =>
        (long)(o.Position?.KeyCount ?? 0) + (o.Rotation?.KeyCount ?? 0) + (o.Scale?.KeyCount ?? 0));
}
