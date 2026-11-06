using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpGLTF.Schema2;

namespace MoodyBlues.Backend.Scenes;

/// <summary>
/// What the session compiler needs to know about one tracked object in a scene revision, read
/// from the uploaded GLB. Transforms are stored exactly as the GLB has them (glTF space).
/// </summary>
public sealed record SceneRegistryObject(
    ushort ObjectId,
    /// <summary>Nearest tracked ancestor, or 0 if none (static ancestors are skipped).</summary>
    ushort ParentObjectId,
    float[] Translation,
    float[] Rotation,
    float[] Scale,
    /// <summary>
    /// The exporter multiplied this node's rotation by a 180 degree turn about Y because the
    /// GameObject has a Camera or Light (UnityGLTF's look-direction flip).
    /// </summary>
    bool InvertLook,
    /// <summary>The GameObject was inactive (activeSelf false) at export.</summary>
    bool Hidden,
    /// <summary>Bounding radius of the node's subtree geometry in its own local space, in scene units.</summary>
    float Radius);

/// <summary>The per-revision object registry, saved as <c>registry.json</c> next to the GLB.</summary>
public sealed class SceneRegistry
{
    public int Version { get; init; } = 1;

    public List<SceneRegistryObject> Objects { get; init; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public Dictionary<ushort, SceneRegistryObject> ToDictionary() => Objects.ToDictionary(o => o.ObjectId);

    public async Task SaveAsync(string fullPath, CancellationToken cancellationToken = default) =>
        await Storage.StoragePaths.WriteAtomicAsync(fullPath, s => JsonSerializer.SerializeAsync(s, this, JsonOptions, cancellationToken), cancellationToken);

    public static async Task<SceneRegistry> LoadAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(fullPath);
        return await JsonSerializer.DeserializeAsync<SceneRegistry>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException($"Empty scene registry: {fullPath}");
    }

    /// <summary>
    /// Builds the registry from a GLB exported by the Unity client: every node whose
    /// <c>extras.objectId</c> is set becomes an entry.
    /// </summary>
    public static SceneRegistry BuildFromGlb(string glbPath)
    {
        ModelRoot model = ModelRoot.Load(glbPath, new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.Skip });

        var radius = new Dictionary<Node, float>();
        foreach (Node root in model.LogicalScenes.SelectMany(s => s.VisualChildren))
        {
            ComputeSubtreeRadius(root, radius);
        }

        var objects = new List<SceneRegistryObject>();
        var seen = new HashSet<ushort>();
        foreach (Node node in model.LogicalNodes)
        {
            ExtrasInfo extras = ReadExtras(node);
            if (extras.ObjectId is not { } objectId || !seen.Add(objectId))
            {
                continue;
            }

            ushort parentId = 0;
            for (Node? p = node.VisualParent; p is not null; p = p.VisualParent)
            {
                if (ReadExtras(p).ObjectId is { } pid)
                {
                    parentId = pid;
                    break;
                }
            }

            var local = node.LocalTransform;
            Vector3 t = local.Translation;
            Quaternion r = Quaternion.Normalize(local.Rotation);
            Vector3 s = local.Scale;
            bool invertLook = extras.InvertLook ?? (node.Camera is not null || node.PunctualLight is not null);

            objects.Add(new SceneRegistryObject(
                objectId,
                parentId,
                [t.X, t.Y, t.Z],
                [r.X, r.Y, r.Z, r.W],
                [s.X, s.Y, s.Z],
                invertLook,
                extras.Hidden,
                radius.GetValueOrDefault(node)));
        }

        objects.Sort((a, b) => a.ObjectId.CompareTo(b.ObjectId));
        return new SceneRegistry { Objects = objects };
    }

    /// <summary>
    /// Conservative bounding radius of a node's subtree in that node's local space: the larger of
    /// its own mesh's radius and each child's offset plus the child's (scaled) radius.
    /// </summary>
    private static float ComputeSubtreeRadius(Node node, Dictionary<Node, float> cache)
    {
        float r = 0f;
        if (node.Mesh is not null)
        {
            foreach (MeshPrimitive primitive in node.Mesh.Primitives)
            {
                Accessor? positions = primitive.GetVertexAccessor("POSITION");
                if (positions is null)
                {
                    continue;
                }

                foreach (Vector3 v in positions.AsVector3Array())
                {
                    r = MathF.Max(r, v.Length());
                }
            }
        }

        foreach (Node child in node.VisualChildren)
        {
            float childRadius = ComputeSubtreeRadius(child, cache);
            var t = child.LocalTransform;
            float maxScale = MathF.Max(MathF.Abs(t.Scale.X), MathF.Max(MathF.Abs(t.Scale.Y), MathF.Abs(t.Scale.Z)));
            r = MathF.Max(r, t.Translation.Length() + childRadius * maxScale);
        }

        cache[node] = r;
        return r;
    }

    private readonly record struct ExtrasInfo(ushort? ObjectId, bool Hidden, bool? InvertLook);

    private static ExtrasInfo ReadExtras(Node node)
    {
        if (node.Extras is null)
        {
            return default;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(node.Extras.ToJsonString());
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            ushort? id = null;
            if (root.TryGetProperty("objectId", out JsonElement idElement) &&
                idElement.ValueKind == JsonValueKind.Number &&
                idElement.TryGetInt32(out int intId) && intId is > 0 and <= ushort.MaxValue)
            {
                id = (ushort)intId;
            }

            bool hidden = root.TryGetProperty("isHidden", out JsonElement h) && h.ValueKind == JsonValueKind.True;
            bool? invertLook = root.TryGetProperty("invertLook", out JsonElement il)
                ? il.ValueKind == JsonValueKind.True
                : null;
            return new ExtrasInfo(id, hidden, invertLook);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
