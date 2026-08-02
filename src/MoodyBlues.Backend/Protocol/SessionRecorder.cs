using System.Numerics;

namespace MoodyBlues.Backend.Protocol;

/// <summary>Which lifecycle transition a <see cref="TimelineSample"/> represents, if any.</summary>
public enum LifecycleEventType
{
    Spawn,
    Show,
    Hide,
    Delete,
}

/// <summary>
    /// A fully-resolved object state at one point in time. Recorded whenever anything about an
    /// object changes -- deliberately a full snapshot (not just the changed property) even though
    /// most wire events only touch one or two properties, so <c>Session/AnimationBuilder.cs</c> can
    /// build every channel from the same sample list without having to track "last known value"
    /// itself.
    /// </summary>
    public sealed record TimelineSample(
        double Time,
        ushort ObjectId,
        ushort? ParentObjectId,
        Vector3 Position,
        Quaternion Rotation,
        Vector3 Scale,
        bool Visible,
        LifecycleEventType? Lifecycle);

/// <summary>Everything <see cref="SessionRecorder"/> accumulated for one connection, once finalized.</summary>
public sealed record SessionTimeline(
    IReadOnlyList<TimelineSample> Samples,
    IReadOnlyList<ushort> ObjectOrder,
    IReadOnlyDictionary<ushort, ushort> TemplateObjectIdByObjectId,
    IReadOnlyDictionary<ushort, ushort> ParentObjectIdByObjectId,
    double DurationSeconds,
    long RawEventCount,
    /// <summary>
    /// Concatenated Unity-wire bytes for lifecycle events only (types 24–28), uncompressed.
    /// Transform events (1–23) are stripped. Gzipped to disk by <c>SessionFinalizer</c>.
    /// </summary>
    ReadOnlyMemory<byte> StrippedEventLog);

/// <summary>
/// Per-connection object-state tracker. Applies every decoded wire event (Spec.md Section 5) to a
/// running per-object state (position/rotation/scale/visibility), exactly mirroring what the
/// Unity client already computed client-side (True* events are absolute, Delta* events are
/// cumulative -- see BluesSessionManager.FixedUpdate/TransformEventDispatcher on the Unity side),
/// and records a full-state snapshot every time anything changes. <see cref="Finalize"/> is called
/// once the WebSocket closes; the resulting timeline feeds <c>Session/AnimationBuilder.cs</c>.
/// </summary>
public sealed class SessionRecorder
{
    private sealed class ObjectState
    {
        // NOTE: these defaults are a known limitation, not a considered "resting" value -- the
        // wire protocol never transmits an object's true starting transform (BluesSessionManager
        // only ever sends a Delta/True event once something *changes* relative to Unity's
        // in-memory LastSent*, which was seeded from the object's actual startup transform, but
        // that startup value itself is never put on the wire). For any scene object whose very
        // first recorded event is a Delta (the common case), everything reconstructed here is
        // consistently off by "true startup transform minus these defaults" -- the *shape* of the
        // motion is right, but the absolute values are anchored to (0,0,0)/identity/(1,1,1)
        // instead of wherever that object actually started. The frontend's GLB export does carry
        // each scene object's true starting transform (baked into the node's TRS), so a future
        // fix could offset-correct per-object using that -- out of scope for this milestone.
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.Identity;
        public Vector3 Scale = Vector3.One;
        public bool Visible = true;
    }

    private readonly Dictionary<ushort, ObjectState> _states = new();
    private readonly Dictionary<ushort, ushort> _templateByObjectId = new();
    private readonly Dictionary<ushort, ushort> _parentByObjectId = new();
    private readonly List<ushort> _objectOrder = new();
    private readonly HashSet<ushort> _seenObjects = new();
    private readonly List<TimelineSample> _samples = new();
    private readonly MemoryStream _strippedEventLog = new();

    // TimeStamp events arrive once per tick (only if something changed that tick) or once per
    // lifecycle event, and apply to every following envelope event until the next TimeStamp --
    // see BluesSessionManager.FixedUpdate / BluesStreamer.EnqueueTimeStampForLifecycleEvent on the
    // Unity side. This is that cursor.
    private double _currentTime;

    // True after a TimeStamp until we either write it into the stripped log (ahead of a lifecycle
    // event) or a newer TimeStamp replaces it. Transform-only ticks never flush into the log.
    private bool _strippedNeedsTimeStamp;

    public long RawEventCount { get; private set; }

    /// <param name="evt">Decoded wire event.</param>
    /// <param name="originalWireBytes">
    /// Optional original wire bytes for <paramref name="evt"/>. When present for lifecycle events,
    /// those bytes are copied into the stripped log (avoids a second quantize pass on Instantiates).
    /// TimeStamp re-encode still uses <see cref="EventLogEncoder"/> so transform-only ticks can be dropped.
    /// </param>
    public void Record(MoodyEvent evt, ReadOnlySpan<byte> originalWireBytes = default)
    {
        RawEventCount++;

        switch (evt)
        {
            case TimeStampEvent ts:
                _currentTime = ts.Seconds;
                _strippedNeedsTimeStamp = true;
                return;

            case ShowObjectEvent show:
                AppendLifecycleToStrippedLog(evt, originalWireBytes);
                GetOrCreateState(show.ObjectId).Visible = true;
                Emit(show.ObjectId, LifecycleEventType.Show);
                return;

            case HideObjectEvent hide:
                AppendLifecycleToStrippedLog(evt, originalWireBytes);
                GetOrCreateState(hide.ObjectId).Visible = false;
                Emit(hide.ObjectId, LifecycleEventType.Hide);
                return;

            case DeleteObjectEvent delete:
                AppendLifecycleToStrippedLog(evt, originalWireBytes);
                Emit(delete.ObjectId, LifecycleEventType.Delete);
                _states.Remove(delete.ObjectId);
                return;

            case InstantiateObjectEvent inst:
            {
                AppendLifecycleToStrippedLog(evt, originalWireBytes);
                var state = GetOrCreateState(inst.ObjectId);
                state.Position = inst.Position;
                state.Rotation = inst.Rotation;
                state.Scale = inst.Scale;
                _templateByObjectId[inst.ObjectId] = inst.TemplateObjectId;
                _parentByObjectId[inst.ObjectId] = inst.ParentObjectId;
                Emit(inst.ObjectId, LifecycleEventType.Spawn);
                return;
            }

            case IObjectEvent objEvt:
            {
                var state = GetOrCreateState(objEvt.ObjectId);
                ApplyTransform(state, evt);
                Emit(objEvt.ObjectId, lifecycle: null);
                return;
            }
        }
    }

    public SessionTimeline Finalize()
    {
        return new SessionTimeline(
            _samples,
            _objectOrder,
            _templateByObjectId,
            _parentByObjectId,
            _currentTime,
            RawEventCount,
            _strippedEventLog.ToArray());
    }

    private void AppendLifecycleToStrippedLog(MoodyEvent lifecycleEvent, ReadOnlySpan<byte> originalWireBytes)
    {
        Span<byte> scratch = stackalloc byte[EventLogEncoder.MaxEventSize];

        if (_strippedNeedsTimeStamp)
        {
            int tsLen = EventLogEncoder.EncodeTimeStamp(_currentTime, scratch);
            _strippedEventLog.Write(scratch[..tsLen]);
            _strippedNeedsTimeStamp = false;
        }

        if (!originalWireBytes.IsEmpty)
        {
            _strippedEventLog.Write(originalWireBytes);
            return;
        }

        int len = EventLogEncoder.Encode(lifecycleEvent, scratch);
        _strippedEventLog.Write(scratch[..len]);
    }

    private ObjectState GetOrCreateState(ushort objectId)
    {
        if (!_states.TryGetValue(objectId, out var state))
        {
            state = new ObjectState();
            _states[objectId] = state;
        }

        if (_seenObjects.Add(objectId))
        {
            _objectOrder.Add(objectId);
        }

        return state;
    }

    private void Emit(ushort objectId, LifecycleEventType? lifecycle, ushort? parentObjectId = null)
    {
        // The object may already have been removed from _states (e.g. Delete already ran) --
        // GetOrCreateState is safe to call again since we still want a sample recorded for the
        // transition itself, using whatever state existed at deletion time.
        var state = _states.TryGetValue(objectId, out var existing) ? existing : GetOrCreateState(objectId);
        _samples.Add(new TimelineSample(_currentTime, objectId, parentObjectId, state.Position, state.Rotation, state.Scale, state.Visible, lifecycle));
    }

    /// <summary>
    /// Applies one True*/Delta* transform event to <paramref name="state"/>. True* events are
    /// absolute; Delta* events are cumulative (state = state OP delta), exactly mirroring the
    /// math BluesStreamer/TransformEventDispatcher used to compute the delta on the Unity side.
    /// </summary>
    private static void ApplyTransform(ObjectState state, MoodyEvent evt)
    {
        switch (evt)
        {
            case TruePositionEvent e:
                state.Position = e.Position;
                break;
            case TrueRotationEvent e:
                state.Rotation = e.Rotation;
                break;
            case TrueRotationSingleAxisEvent e:
                state.Rotation = QuaternionFromAxisAngle(e.Axis, e.AngleDeg);
                break;
            case TrueScaleEvent e:
                state.Scale = e.Scale;
                break;
            case TrueUniformScaleEvent e:
                state.Scale = new Vector3(e.Scale, e.Scale, e.Scale);
                break;

            case DeltaPositionEvent e:
                state.Position += e.DeltaPosition;
                break;
            case DeltaRotationEvent e:
                state.Rotation = state.Rotation * e.DeltaRotation;
                break;
            case DeltaRotationSingleAxisEvent e:
                state.Rotation = state.Rotation * QuaternionFromAxisAngle(e.Axis, e.DeltaAngleDeg);
                break;
            case DeltaScaleEvent e:
                state.Scale += e.DeltaScale;
                break;
            case DeltaUniformScaleEvent e:
                state.Scale += new Vector3(e.DeltaScale, e.DeltaScale, e.DeltaScale);
                break;

            case TrueTransformEvent e:
                state.Position = e.Position;
                state.Rotation = e.Rotation;
                state.Scale = e.Scale;
                break;
            case TruePositionRotationEvent e:
                state.Position = e.Position;
                state.Rotation = e.Rotation;
                break;
            case TrueRotationScaleEvent e:
                state.Rotation = e.Rotation;
                state.Scale = e.Scale;
                break;
            case TruePositionScaleEvent e:
                state.Position = e.Position;
                state.Scale = e.Scale;
                break;
            case TruePositionRotationUniformScaleEvent e:
                state.Position = e.Position;
                state.Rotation = e.Rotation;
                state.Scale = new Vector3(e.UniformScale, e.UniformScale, e.UniformScale);
                break;
            case TruePositionRotationSingleAxisEvent e:
                state.Position = e.Position;
                state.Rotation = QuaternionFromAxisAngle(e.Axis, e.AngleDeg);
                break;

            case DeltaTransformEvent e:
                state.Position += e.DeltaPosition;
                state.Rotation = state.Rotation * e.DeltaRotation;
                state.Scale += e.DeltaScale;
                break;
            case DeltaPositionRotationEvent e:
                state.Position += e.DeltaPosition;
                state.Rotation = state.Rotation * e.DeltaRotation;
                break;
            case DeltaRotationScaleEvent e:
                state.Rotation = state.Rotation * e.DeltaRotation;
                state.Scale += e.DeltaScale;
                break;
            case DeltaPositionScaleEvent e:
                state.Position += e.DeltaPosition;
                state.Scale += e.DeltaScale;
                break;
            case DeltaPositionRotationSingleAxisEvent e:
                state.Position += e.DeltaPosition;
                state.Rotation = state.Rotation * QuaternionFromAxisAngle(e.Axis, e.DeltaAngleDeg);
                break;
            case DeltaPositionUniformScaleEvent e:
                state.Position += e.DeltaPosition;
                state.Scale += new Vector3(e.DeltaUniformScale, e.DeltaUniformScale, e.DeltaUniformScale);
                break;
            case DeltaRotationUniformScaleEvent e:
                state.Rotation = state.Rotation * e.DeltaRotation;
                state.Scale += new Vector3(e.DeltaUniformScale, e.DeltaUniformScale, e.DeltaUniformScale);
                break;
        }
    }

    private static Quaternion QuaternionFromAxisAngle(string axis, float angleDeg)
    {
        float rad = angleDeg * (MathF.PI / 180f);
        Vector3 axisVector = axis switch
        {
            "X" => Vector3.UnitX,
            "Y" => Vector3.UnitY,
            "Z" => Vector3.UnitZ,
            _ => Vector3.UnitX,
        };
        return Quaternion.CreateFromAxisAngle(axisVector, rad);
    }
}
