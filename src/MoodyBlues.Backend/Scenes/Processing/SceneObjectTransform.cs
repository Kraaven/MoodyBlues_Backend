using System.Numerics;

namespace MoodyBlues.Backend.Scenes.Processing;

/// <summary>Rest-pose local transform for one scene object, keyed by wire ObjectId from node.extras.</summary>
public sealed record SceneObjectTransform(
    ushort ObjectId,
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Scale);
