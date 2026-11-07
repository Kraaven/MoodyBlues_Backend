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
    /// <summary>Largest node count accepted in an uploaded GLB.</summary>
    public const int MaxNodes = 250_000;

    /// <summary>Deepest hierarchy accepted in an uploaded GLB.</summary>
    public const int MaxDepth = 2048;

    public static SceneRegistry BuildFromGlb(string glbPath)
    {
        ModelRoot model = ModelRoot.Load(glbPath, new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.Skip });

        if (model.LogicalNodes.Count > MaxNodes)
        {
            throw new InvalidDataException($"The scene has {model.LogicalNodes.Count} nodes; at most {MaxNodes} are supported.");
        }

        // The GLB is untrusted input, so the hierarchy is walked without recursion and checked
        // for cycles and excessive depth: a crafted file must fail cleanly, not overflow the stack.
        List<Node> order = WalkHierarchy(model, out Dictionary<Node, ushort> trackedParent);
        Dictionary<Node, float> radius = ComputeRadii(order);

        var objects = new List<SceneRegistryObject>();
        var seen = new HashSet<ushort>();
        foreach (Node node in order)
        {
            ExtrasInfo extras = ReadExtras(node);
            if (extras.ObjectId is not { } objectId || !seen.Add(objectId))
            {
                continue;
            }

            var local = node.LocalTransform;
            Vector3 t = local.Translation;
            Quaternion r = Quaternion.Normalize(local.Rotation);
            Vector3 s = local.Scale;
            bool invertLook = extras.InvertLook ?? (node.Camera is not null || node.PunctualLight is not null);

            objects.Add(new SceneRegistryObject(
                objectId,
                trackedParent.GetValueOrDefault(node),
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
    /// Depth-first pre-order of every node reachable from the scenes' roots, plus each node's
    /// nearest ancestor that has an ObjectID (0 if none).
    /// </summary>
    private static List<Node> WalkHierarchy(ModelRoot model, out Dictionary<Node, ushort> trackedParent)
    {
        var order = new List<Node>();
        var visited = new HashSet<Node>();
        trackedParent = new Dictionary<Node, ushort>();
        var stack = new Stack<(Node Node, ushort TrackedAncestor, int Depth)>();

        foreach (Node root in model.LogicalScenes.SelectMany(s => s.VisualChildren).Reverse())
        {
            stack.Push((root, 0, 0));
        }

        while (stack.Count > 0)
        {
            (Node node, ushort ancestor, int depth) = stack.Pop();
            if (!visited.Add(node))
            {
                throw new InvalidDataException("The scene hierarchy contains a cycle or a node with two parents.");
            }

            if (depth > MaxDepth)
            {
                throw new InvalidDataException($"The scene hierarchy is deeper than {MaxDepth} levels.");
            }

            order.Add(node);
            trackedParent[node] = ancestor;
            ushort childAncestor = ReadExtras(node).ObjectId ?? ancestor;

            foreach (Node child in node.VisualChildren.Reverse())
            {
                stack.Push((child, childAncestor, depth + 1));
            }
        }

        return order;
    }

    /// <summary>
    /// Conservative bounding radius of each node's subtree in that node's local space: the larger
    /// of its own mesh's radius and each child's offset plus the child's (scaled) radius.
    /// Children come after their parent in <paramref name="preOrder"/>, so walking it backwards
    /// visits every child before its parent.
    /// </summary>
    private static Dictionary<Node, float> ComputeRadii(List<Node> preOrder)
    {
        var radius = new Dictionary<Node, float>(preOrder.Count);
        for (int i = preOrder.Count - 1; i >= 0; i--)
        {
            Node node = preOrder[i];
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
                        float length = v.Length();
                        if (float.IsFinite(length)) r = MathF.Max(r, length);
                    }
                }
            }

            foreach (Node child in node.VisualChildren)
            {
                var t = child.LocalTransform;
                float maxScale = MathF.Max(MathF.Abs(t.Scale.X), MathF.Max(MathF.Abs(t.Scale.Y), MathF.Abs(t.Scale.Z)));
                float extent = t.Translation.Length() + (radius.GetValueOrDefault(child) * maxScale);
                if (float.IsFinite(extent)) r = MathF.Max(r, extent);
            }

            radius[node] = r;
        }

        return radius;
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
