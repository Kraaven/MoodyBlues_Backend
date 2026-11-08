using System.Buffers.Binary;
using System.Numerics;
using MoodyBlues.Backend.Protocol;
using MoodyBlues.Backend.Scenes;
using MoodyBlues.Backend.Storage;

namespace MoodyBlues.Backend.Compiler;

/// <summary>Accuracy targets for the curve fitter.</summary>
public sealed record FitTolerances
{
    /// <summary>
    /// Maximum visible error the fitting may add, as a displacement in scene units (1 mm).
    /// Rotation and scale are converted to this using each object's bounding radius, so a 0.1
    /// degree error costs more on a bridge than on a coin.
    /// </summary>
    public float SurfaceDistance { get; init; } = 0.001f;

    /// <summary>
    /// Extra allowance as a fraction of the wire's own quantization step, so the fitter does not
    /// spend keys reproducing noise the wire already introduced.
    /// </summary>
    public float QuantizationAllowance { get; init; } = 0.5f;

    /// <summary>Radius used when an object has no geometry information.</summary>
    public float FallbackRadius { get; init; } = 1f;

    public static readonly FitTolerances Default = new();
}

/// <summary>
/// Replays a raw session log through the wire decoder, keeping each object's current local
/// transform exactly as the Unity client does (Unity space; deltas add, rotations post-multiply),
/// and feeds each changed property into a <see cref="ChannelFitter"/> after converting it to glTF
/// space. Also derives the lifecycle event timeline, including the Delete subtree cascade
/// (Unity Spec 10.1), which needs the live hierarchy.
/// </summary>
public sealed class SessionReconstructor : IWireEventSink
{
    private sealed class ObjectState
    {
        public required ushort Id;
        public ReplayObjectKind Kind;
        public bool Known;           // in the registry or spawned; unknown IDs get no tracks
        public bool Alive = true;
        public bool Emitted;         // goes into the replay's object table
        public ushort Template;
        public ushort Parent;
        public ushort InitialParent;
        public double SpawnTime = -1;
        public double DeleteTime = -1;
        public bool InvertLook;
        public float Radius;

        // Current local transform in Unity space (mirrors the client's baseline exactly).
        public Vector3 P;
        public Quaternion R = Quaternion.Identity;
        public Vector3 S = Vector3.One;

        // Seed (start-of-session) transform for scene objects, Unity space.
        public Vector3 SeedP;
        public Quaternion SeedR = Quaternion.Identity;
        public Vector3 SeedS = Vector3.One;

        public ChannelFitter? Pos;
        public ChannelFitter? Rot;
        public ChannelFitter? Scl;
    }

    private readonly Dictionary<ushort, ObjectState> _objects = new();
    private readonly Dictionary<ushort, SceneRegistryObject> _registry;
    private readonly List<SceneRegistryObject> _registryOrdered;
    private readonly Dictionary<ushort, ushort[]> _templateDescendants = new();
    private readonly FitTolerances _tolerances;
    private readonly List<ReplayEvent> _events = [];

    private double? _t0;
    private double _now;
    private double _maxTime;
    private double _tick = 1.0 / 40.0;
    private WireRanges _ranges = WireRanges.Default;
    private bool _rangesKnown;
    private readonly Dictionary<uint, WireDecoder> _decoders = new();
    private uint _latestConnection;
    private int _connectionCount;
    private bool _endedCleanly;
    private SessionEndReason? _endReason;
    private EndSessionInfo _endInfo;
    private long _rawEventCount;
    private long _unknownObjectEvents;
    private int _spawnCount;

    // Log reassembly (Spec 6.7): a Log event with a non-zero level starts a log, level 0 appends.
    private const int MaxLogBytes = 64 * 1024;
    private readonly List<ReplayLog> _logs = [];
    private readonly List<byte> _pendingLogText = [];
    private bool _hasPendingLog;
    private double _pendingLogTime;
    private byte _pendingLogLevel;
    private byte _pendingLogColor;

    public SessionReconstructor(SceneRegistry registry, FitTolerances? tolerances = null)
    {
        _registry = registry.ToDictionary();
        _registryOrdered = registry.Objects.OrderBy(o => o.ObjectId).ToList();
        _tolerances = tolerances ?? FitTolerances.Default;

        foreach (SceneRegistryObject o in registry.Objects)
        {
            var seedP = CoordinateSpace.PositionGltfToUnity(new Vector3(o.Translation[0], o.Translation[1], o.Translation[2]));
            var seedR = CoordinateSpace.RotationGltfToUnity(new Quaternion(o.Rotation[0], o.Rotation[1], o.Rotation[2], o.Rotation[3]), o.InvertLook);
            var seedS = new Vector3(o.Scale[0], o.Scale[1], o.Scale[2]);
            _objects[o.ObjectId] = new ObjectState
            {
                Id = o.ObjectId,
                Kind = ReplayObjectKind.Scene,
                Known = true,
                Parent = o.ParentObjectId,
                InitialParent = o.ParentObjectId,
                InvertLook = o.InvertLook,
                Radius = o.Radius,
                P = seedP,
                R = seedR,
                S = seedS,
                SeedP = seedP,
                SeedR = seedR,
                SeedS = seedS,
            };
        }
    }

    /// <summary>Feeds every record of a raw log, then finishes and returns the replay.</summary>
    public CompiledReplay Run(IEnumerable<RawLogRecord> records, CancellationToken cancellationToken = default)
    {
        int count = 0;
        foreach (RawLogRecord record in records)
        {
            if ((++count & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            Apply(record);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Finish();
    }

    public void Apply(RawLogRecord record)
    {
        uint connection = record.Connection;

        switch (record.Kind)
        {
            case RawLogRecordKind.ConnectionOpened:
            {
                uint index = record.Connection;
                _latestConnection = index;
                _decoders[index] = new WireDecoder(requireBeginSession: true);
                _connectionCount++;
                AddMarker(ReplayEventType.Connected, aux: 0, a: (ushort)Math.Min(index, ushort.MaxValue));
                break;
            }

            case RawLogRecordKind.Message:
                if (!_decoders.TryGetValue(connection, out WireDecoder? decoder))
                {
                    decoder = new WireDecoder(requireBeginSession: true);
                    _decoders[connection] = decoder;
                }

                try
                {
                    decoder.DecodeMessage(record.Payload.Span, this);
                }
                catch (ProtocolException)
                {
                    // Messages were validated at ingest with the same rules, so this means the
                    // log is damaged. Record the gap rather than failing.
                    AddMarker(ReplayEventType.DataGap, 0, 0);
                }

                break;

            case RawLogRecordKind.RejectedMessage:
                AddMarker(ReplayEventType.DataGap, 0, 0);
                break;

            case RawLogRecordKind.ConnectionClosed:
                FlushPendingLog();
                AddMarker(ReplayEventType.Disconnected, record.Payload.Length > 0 ? record.Payload.Span[0] : (byte)0, 0);

                // Only this connection's decoder goes. Another connection of the same session may
                // still be open (a reconnect that overlapped the old connection's late close).
                _decoders.Remove(connection);
                break;
        }
    }

    public CompiledReplay Finish()
    {
        FlushPendingLog();

        var objects = new List<ReplayObject>();
        foreach (ObjectState o in _objects.Values.OrderBy(o => o.Id))
        {
            o.Pos?.Flush();
            o.Rot?.Flush();
            o.Scl?.Flush();

            if (!o.Emitted && o.Pos is null && o.Rot is null && o.Scl is null)
            {
                continue;
            }

            objects.Add(new ReplayObject
            {
                ObjectId = o.Id,
                Kind = o.Kind,
                TemplateObjectId = o.Template,
                InitialParentObjectId = o.InitialParent,
                SpawnTime = o.SpawnTime,
                DeleteTime = o.DeleteTime,
                Position = o.Pos,
                Rotation = o.Rot,
                Scale = o.Scl,
            });
        }

        return new CompiledReplay
        {
            DurationSeconds = _maxTime,
            Objects = objects,
            Events = _events,
            Logs = _logs,
            EndedCleanly = _endedCleanly,
            EndReason = _endReason,
            ConnectionCount = _connectionCount,
            RawEventCount = _rawEventCount,
            DeltaPromotions = _endInfo.DeltaPromotions,
            HardClamps = _endInfo.HardClamps,
            DroppedEvents = _endInfo.DroppedEvents,
            UnknownObjectEvents = _unknownObjectEvents,
            SpawnCount = _spawnCount,
        };
    }

    // ---- IWireEventSink ----------------------------------------------------------------------

    public void OnTimeStamp(double seconds)
    {
        _rawEventCount++;
        _t0 ??= seconds;
        _now = Math.Max(0, seconds - _t0.Value);
        _maxTime = Math.Max(_maxTime, _now);
    }

    public void OnBeginSession(in BeginSessionInfo info)
    {
        _rawEventCount++;
        if (info.CaptureHz > 0 && float.IsFinite(info.CaptureHz))
        {
            _tick = 1.0 / info.CaptureHz;
        }

        if (!_rangesKnown)
        {
            _ranges = info.Ranges;
            _rangesKnown = true;
        }
    }

    public void OnKeepAlive() => _rawEventCount++;

    public void OnEndSession(in EndSessionInfo info)
    {
        _rawEventCount++;
        _endedCleanly = true;
        _endReason = info.Reason;
        _endInfo = info;
        AddMarker(ReplayEventType.SessionEnded, (byte)info.Reason, 0);
    }

    public void OnShow(ushort objectId)
    {
        _rawEventCount++;
        ObjectState o = GetOrCreate(objectId);
        o.Emitted = true;
        _events.Add(new ReplayEvent(_now, ReplayEventType.Show, 0, objectId, 0, 0));
    }

    public void OnHide(ushort objectId)
    {
        _rawEventCount++;
        ObjectState o = GetOrCreate(objectId);
        o.Emitted = true;
        _events.Add(new ReplayEvent(_now, ReplayEventType.Hide, 0, objectId, 0, 0));
    }

    public void OnDelete(ushort objectId)
    {
        _rawEventCount++;
        ObjectState root = GetOrCreate(objectId);
        if (!root.Alive)
        {
            return;
        }

        _events.Add(new ReplayEvent(_now, ReplayEventType.Delete, 0, objectId, 0, 0));

        // Cascade to the whole tracked subtree (Spec 10.1): every live object whose ancestor
        // chain, through current parents, reaches the deleted object.
        var doomed = new List<ObjectState> { root };
        foreach (ObjectState o in _objects.Values)
        {
            if (o.Alive && o != root && HasAncestor(o, objectId))
            {
                doomed.Add(o);
            }
        }

        foreach (ObjectState o in doomed)
        {
            o.Alive = false;
            o.DeleteTime = _now;
            o.Emitted = true;
        }
    }

    public void OnInstantiate(ushort objectId, ushort templateObjectId, ushort parentObjectId, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        _rawEventCount++;
        _spawnCount++;

        // IDs are never reused (Spec 5), so an existing entry here is a client bug. Keep the newer
        // object; the replay can only show one object per ID.
        _registry.TryGetValue(templateObjectId, out SceneRegistryObject? template);
        var o = new ObjectState
        {
            Id = objectId,
            Kind = ReplayObjectKind.Spawned,
            Known = template is not null,
            Emitted = true,
            Template = templateObjectId,
            Parent = parentObjectId,
            InitialParent = parentObjectId,
            SpawnTime = _now,
            InvertLook = template?.InvertLook ?? false,
            Radius = template?.Radius ?? 0f,
            P = position,
            R = rotation,
            S = scale,
        };
        _objects[objectId] = o;
        _events.Add(new ReplayEvent(_now, ReplayEventType.Spawn, 0, objectId, templateObjectId, parentObjectId));

        if (o.Known)
        {
            Sample(o, TransformFields.Position | TransformFields.Rotation | TransformFields.Scale, discontinuity: false);
            SpawnSubtree(objectId, templateObjectId);
        }
    }

    /// <summary>
    /// The client registered the clone's whole subtree with consecutive IDs after the
    /// root's, in walk order. The template's tracked descendants, in ascending ObjectID order
    /// (which is walk order), map one to one onto that block. Each starts at its template node's
    /// local pose; the client sends an absolute transform for any that did not.
    /// </summary>
    private void SpawnSubtree(ushort rootId, ushort templateId)
    {
        ushort[] descendants = TemplateDescendants(templateId);
        if (descendants.Length == 0 || rootId + descendants.Length > ushort.MaxValue)
        {
            return;
        }

        var cloneIdOf = new Dictionary<ushort, ushort>(descendants.Length + 1) { [templateId] = rootId };
        for (int i = 0; i < descendants.Length; i++)
        {
            cloneIdOf[descendants[i]] = (ushort)(rootId + 1 + i);
        }

        foreach (ushort templateNode in descendants)
        {
            SceneRegistryObject t = _registry[templateNode];
            ushort id = cloneIdOf[templateNode];
            ushort parent = cloneIdOf.GetValueOrDefault(t.ParentObjectId, rootId);
            var p = CoordinateSpace.PositionGltfToUnity(new Vector3(t.Translation[0], t.Translation[1], t.Translation[2]));
            var r = CoordinateSpace.RotationGltfToUnity(new Quaternion(t.Rotation[0], t.Rotation[1], t.Rotation[2], t.Rotation[3]), t.InvertLook);
            var s = new Vector3(t.Scale[0], t.Scale[1], t.Scale[2]);

            _objects[id] = new ObjectState
            {
                Id = id,
                Kind = ReplayObjectKind.SpawnedPart,
                Known = true,
                Emitted = true,
                Template = templateNode,
                Parent = parent,
                InitialParent = parent,
                SpawnTime = _now,
                InvertLook = t.InvertLook,
                Radius = t.Radius,
                P = p,
                R = r,
                S = s,
                SeedP = p,
                SeedR = r,
                SeedS = s,
            };
        }
    }

    /// <summary>A template's tracked descendants, ascending ObjectID. Cached per template.</summary>
    private ushort[] TemplateDescendants(ushort templateId)
    {
        if (_templateDescendants.TryGetValue(templateId, out ushort[]? cached))
        {
            return cached;
        }

        var result = new List<ushort>();
        foreach (SceneRegistryObject o in _registryOrdered)
        {
            if (o.ObjectId <= templateId) continue;
            if (!HasRegistryAncestor(o, templateId)) break; // a subtree's IDs are contiguous
            result.Add(o.ObjectId);
        }

        ushort[] array = result.ToArray();
        _templateDescendants[templateId] = array;
        return array;
    }

    private bool HasRegistryAncestor(SceneRegistryObject o, ushort ancestorId)
    {
        int guard = 0;
        for (ushort p = o.ParentObjectId; p != 0 && guard++ < 4096; p = _registry.TryGetValue(p, out var parent) ? parent.ParentObjectId : (ushort)0)
        {
            if (p == ancestorId) return true;
        }

        return false;
    }

    public void OnReparent(ushort objectId, ushort newParentObjectId, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        _rawEventCount++;
        ObjectState o = GetOrCreate(objectId);
        if (!o.Alive)
        {
            return;
        }

        o.Parent = newParentObjectId;
        o.Emitted = true;
        o.P = position;
        o.R = rotation;
        o.S = scale;
        _events.Add(new ReplayEvent(_now, ReplayEventType.Reparent, 0, objectId, newParentObjectId, 0));

        // The local transform jumps because the parent changed; keep the old value until the
        // instant of the reparent so the viewer's parent switch and the curve jump coincide.
        if (o.Known)
        {
            Sample(o, TransformFields.Position | TransformFields.Rotation | TransformFields.Scale, discontinuity: true);
        }
    }

    public void OnTransform(ushort objectId, in TransformUpdate update)
    {
        _rawEventCount++;
        ObjectState o = GetOrCreate(objectId);
        if (!o.Alive)
        {
            return;
        }

        if (update.Has(TransformFields.Position))
        {
            o.P = update.Has(TransformFields.PositionIsDelta) ? o.P + update.Position : update.Position;
        }

        if (update.Has(TransformFields.Rotation))
        {
            o.R = update.Has(TransformFields.RotationIsDelta) ? o.R * update.Rotation : update.Rotation;
        }

        if (update.Has(TransformFields.Scale))
        {
            o.S = update.Has(TransformFields.ScaleIsDelta) ? o.S + update.Scale : update.Scale;
        }

        if (!o.Known)
        {
            _unknownObjectEvents++;
            return;
        }

        Sample(o, update.Fields, discontinuity: false);
    }

    public void OnLog(byte style, ReadOnlySpan<byte> utf8Text)
    {
        _rawEventCount++;
        byte level = (byte)(style >> 6);

        if (level != 0 || !_hasPendingLog)
        {
            // A continuation with nothing to continue (its first event was dropped on the client)
            // still carries text worth keeping, so it becomes a plain info log.
            FlushPendingLog();
            _hasPendingLog = true;
            _pendingLogTime = _now;
            _pendingLogLevel = level != 0 ? level : (byte)1;
            _pendingLogColor = level != 0 ? (byte)(style & 0x3F) : (byte)0;
        }

        int room = MaxLogBytes - _pendingLogText.Count;
        if (room <= 0) return;
        if (utf8Text.Length > room) utf8Text = utf8Text[..room];
        foreach (byte b in utf8Text) _pendingLogText.Add(b);
    }

    private void FlushPendingLog()
    {
        if (!_hasPendingLog) return;

        string text = System.Text.Encoding.UTF8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pendingLogText));
        _logs.Add(new ReplayLog(_pendingLogTime, _pendingLogLevel, _pendingLogColor, text));
        _pendingLogText.Clear();
        _hasPendingLog = false;
    }

    // ---- internals ----------------------------------------------------------------------------

    private ObjectState GetOrCreate(ushort id)
    {
        if (!_objects.TryGetValue(id, out ObjectState? o))
        {
            o = new ObjectState { Id = id, Kind = ReplayObjectKind.Scene, Known = false };
            _objects[id] = o;
        }

        return o;
    }

    private bool HasAncestor(ObjectState o, ushort ancestorId)
    {
        int guard = 0;
        ushort p = o.Parent;
        while (p != 0 && guard++ < 4096)
        {
            if (p == ancestorId)
            {
                return true;
            }

            if (!_objects.TryGetValue(p, out ObjectState? parent))
            {
                return false;
            }

            p = parent.Parent;
        }

        return false;
    }

    private void AddMarker(ReplayEventType type, byte aux, ushort a) =>
        _events.Add(new ReplayEvent(_now, type, aux, 0, a, 0));

    private void Sample(ObjectState o, TransformFields fields, bool discontinuity)
    {
        Span<float> v = stackalloc float[4];

        if ((fields & TransformFields.Position) != 0)
        {
            o.Pos ??= CreateFitter(o, ReplayChannel.Position);
            Write(CoordinateSpace.PositionUnityToGltf(o.P), v);
            o.Pos.Add(_now, v, discontinuity);
        }

        if ((fields & TransformFields.Rotation) != 0)
        {
            o.Rot ??= CreateFitter(o, ReplayChannel.Rotation);
            Write(Quaternion.Normalize(CoordinateSpace.RotationUnityToGltf(o.R, o.InvertLook)), v);
            o.Rot.Add(_now, v, discontinuity);
        }

        if ((fields & TransformFields.Scale) != 0)
        {
            o.Scl ??= CreateFitter(o, ReplayChannel.Scale);
            Write(o.S, v);
            o.Scl.Add(_now, v, discontinuity);
        }
    }

    /// <summary>
    /// Creates a fitter. A scene object's first change needs a key at session start holding its
    /// export pose, otherwise the viewer would interpolate from time 0 toward the first change.
    /// </summary>
    private ChannelFitter CreateFitter(ObjectState o, ReplayChannel channel)
    {
        float radius = o.Radius > 0 ? o.Radius : _tolerances.FallbackRadius;
        float surface = _tolerances.SurfaceDistance;
        float allowance = _tolerances.QuantizationAllowance;

        ChannelFitter fitter = channel switch
        {
            ReplayChannel.Position => new ChannelFitter(3, false, surface + (allowance * _ranges.DeltaPositionStep), _tick),
            ReplayChannel.Rotation => new ChannelFitter(
                4,
                true,
                Math.Clamp(surface / radius, 1e-4f, 0.02f) + (allowance * _ranges.DeltaRotationComponentStep),
                _tick),
            _ => new ChannelFitter(3, false, Math.Clamp(surface / radius, 1e-4f, 0.01f) + (allowance * _ranges.DeltaScaleStep), _tick),
        };

        if (o.Kind == ReplayObjectKind.Scene && _now > 0)
        {
            AddSeedKey(fitter, o, channel, 0);
        }
        else if (o.Kind == ReplayObjectKind.SpawnedPart && _now > o.SpawnTime)
        {
            // A clone child holds its template pose from its spawn until it first moves.
            AddSeedKey(fitter, o, channel, o.SpawnTime);
        }

        return fitter;
    }

    private static void AddSeedKey(ChannelFitter fitter, ObjectState o, ReplayChannel channel, double time)
    {
        Span<float> v = stackalloc float[4];
        switch (channel)
        {
            case ReplayChannel.Position:
                Write(CoordinateSpace.PositionUnityToGltf(o.SeedP), v);
                break;
            case ReplayChannel.Rotation:
                Write(Quaternion.Normalize(CoordinateSpace.RotationUnityToGltf(o.SeedR, o.InvertLook)), v);
                break;
            default:
                Write(o.SeedS, v);
                break;
        }

        fitter.Add(time, v);
    }

    private static void Write(Vector3 value, Span<float> dest)
    {
        dest[0] = value.X;
        dest[1] = value.Y;
        dest[2] = value.Z;
    }

    private static void Write(Quaternion value, Span<float> dest)
    {
        dest[0] = value.X;
        dest[1] = value.Y;
        dest[2] = value.Z;
        dest[3] = value.W;
    }
}
