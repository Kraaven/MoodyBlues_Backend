using System.Numerics;
using MoodyBlues.Backend.Scenes;

namespace MoodyBlues.Backend.Tests;

public class SceneRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mb-registry-tests-" + Guid.NewGuid().ToString("N"));

    public SceneRegistryTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// A small glTF shaped like a UnityGLTF export: tracked nodes carry extras.objectId, an
    /// untracked (static) node sits between a tracked parent and a tracked child, a camera is
    /// flagged invertLook, and an inactive empty leaf is flagged isHidden.
    /// </summary>
    private string WriteScene()
    {
        // One triangle with vertices up to 1 unit from its node's origin.
        float[] positions = [1, 0, 0, 0, 1, 0, 0, 0, 0];
        byte[] bytes = new byte[positions.Length * 4];
        Buffer.BlockCopy(positions, 0, bytes, 0, bytes.Length);
        string data = Convert.ToBase64String(bytes);

        string json = $$"""
        {
          "asset": { "version": "2.0" },
          "scene": 0,
          "scenes": [ { "nodes": [0, 3, 4] } ],
          "nodes": [
            { "name": "Root", "children": [1], "translation": [-2, 0, 0], "extras": { "objectId": 1 } },
            { "name": "Static", "children": [2], "translation": [0, 0, 3], "mesh": 0 },
            { "name": "Child", "translation": [0, 1, 0], "mesh": 0, "extras": { "objectId": 3 } },
            { "name": "Cam", "rotation": [0, 1, 0, 0], "camera": 0, "extras": { "objectId": 4, "invertLook": true } },
            { "name": "Hidden", "extras": { "objectId": 5, "isHidden": true } }
          ],
          "cameras": [ { "type": "perspective", "perspective": { "yfov": 1.0, "znear": 0.1 } } ],
          "meshes": [ { "primitives": [ { "attributes": { "POSITION": 0 } } ] } ],
          "accessors": [ { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 0], "max": [1, 1, 0] } ],
          "bufferViews": [ { "buffer": 0, "byteLength": {{bytes.Length}} } ],
          "buffers": [ { "byteLength": {{bytes.Length}}, "uri": "data:application/octet-stream;base64,{{data}}" } ]
        }
        """;
        string path = Path.Combine(_dir, "scene.gltf");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Build_ReadsTrackedNodes_ParentsThroughStaticNodes_AndFlags()
    {
        SceneRegistry registry = SceneRegistry.BuildFromGlb(WriteScene());
        Dictionary<ushort, SceneRegistryObject> byId = registry.ToDictionary();

        Assert.Equal([1, 3, 4, 5], byId.Keys.Order().Select(k => (int)k));
        Assert.Equal(0, byId[1].ParentObjectId);
        Assert.Equal(1, byId[3].ParentObjectId); // through the untracked "Static" node
        Assert.Equal([-2f, 0f, 0f], byId[1].Translation);
        Assert.True(byId[4].InvertLook);
        Assert.False(byId[1].InvertLook);
        Assert.True(byId[5].Hidden);

        // Root's subtree: Static is 3 units away and its own mesh reaches 1 more, Child adds 1 + 1.
        Assert.InRange(byId[1].Radius, 3.9f, 5.2f);
        Assert.Equal(1f, byId[3].Radius, 3);
        Assert.Equal(0f, byId[5].Radius);
    }

    [Fact]
    public async Task Registry_RoundTripsThroughJson()
    {
        SceneRegistry registry = SceneRegistry.BuildFromGlb(WriteScene());
        string path = Path.Combine(_dir, "registry.json");

        await registry.SaveAsync(path);
        SceneRegistry loaded = await SceneRegistry.LoadAsync(path);

        Assert.Equal(registry.Objects.Count, loaded.Objects.Count);
        Assert.Equal(registry.Objects[2].Rotation, loaded.Objects[2].Rotation);
        Assert.Equal(registry.Objects[0].ParentObjectId, loaded.Objects[0].ParentObjectId);
    }

    [Fact]
    public void SeedFromRegistry_UndoesTheExportConversion()
    {
        // Unity local position (2, 0, 0) is exported as glTF (-2, 0, 0); the compiler must seed
        // the Unity-space state with (2, 0, 0) so Unity-space deltas apply correctly.
        Vector3 unity = Compiler.CoordinateSpace.PositionGltfToUnity(new Vector3(-2, 0, 0));
        Assert.Equal(new Vector3(2, 0, 0), unity);
    }
}
