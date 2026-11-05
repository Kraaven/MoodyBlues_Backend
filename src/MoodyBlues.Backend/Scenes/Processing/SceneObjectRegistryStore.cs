using System.Text.Json;
using MoodyBlues.Backend.Config;

namespace MoodyBlues.Backend.Scenes.Processing;

/// <summary>Persists and loads per-scene object registries as JSON sidecars next to the GLB files.</summary>
public sealed class SceneObjectRegistryStore(ServerConfig config)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string GetRegistryPath(string developerId, string sceneId) =>
        Path.Combine(config.ScenesDir, developerId, $"{sceneId}.registry.json");

    public void Save(string developerId, string sceneId, IReadOnlyDictionary<ushort, SceneObjectTransform> registry)
    {
        string path = GetRegistryPath(developerId, sceneId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var dto = registry.Values
            .OrderBy(t => t.ObjectId)
            .Select(t => new RegistryEntryDto(
                t.ObjectId,
                t.Position.X, t.Position.Y, t.Position.Z,
                t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W,
                t.Scale.X, t.Scale.Y, t.Scale.Z))
            .ToList();

        File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOptions));
    }

    public IReadOnlyDictionary<ushort, SceneObjectTransform> Load(string developerId, string sceneId)
    {
        string path = GetRegistryPath(developerId, sceneId);
        if (!File.Exists(path))
        {
            return new Dictionary<ushort, SceneObjectTransform>();
        }

        try
        {
            List<RegistryEntryDto>? entries = JsonSerializer.Deserialize<List<RegistryEntryDto>>(File.ReadAllText(path), JsonOptions);
            if (entries is null)
            {
                return new Dictionary<ushort, SceneObjectTransform>();
            }

            return entries.ToDictionary(
                e => e.ObjectId,
                e => new SceneObjectTransform(
                    e.ObjectId,
                    new System.Numerics.Vector3(e.Px, e.Py, e.Pz),
                    new System.Numerics.Quaternion(e.Rx, e.Ry, e.Rz, e.Rw),
                    new System.Numerics.Vector3(e.Sx, e.Sy, e.Sz)));
        }
        catch
        {
            return new Dictionary<ushort, SceneObjectTransform>();
        }
    }

    private sealed record RegistryEntryDto(
        ushort ObjectId,
        float Px, float Py, float Pz,
        float Rx, float Ry, float Rz, float Rw,
        float Sx, float Sy, float Sz);
}
