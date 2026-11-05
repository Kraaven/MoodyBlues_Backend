using System.Numerics;
using System.Text.Json;
using MoodyBlues.Backend.Logging;
using SharpGLTF.Schema2;

namespace MoodyBlues.Backend.Scenes.Processing;

/// <summary>
/// Parses an uploaded scene GLB and extracts each node's <c>extras.objectId</c> plus local TRS,
/// producing the registry used to seed <see cref="Protocol.SessionRecorder"/> object state.
/// </summary>
public static class SceneObjectRegistryBuilder
{
    public static IReadOnlyDictionary<ushort, SceneObjectTransform> BuildFromGlb(string glbPath)
    {
        var registry = new Dictionary<ushort, SceneObjectTransform>();

        if (!File.Exists(glbPath))
        {
            return registry;
        }

        try
        {
            ModelRoot model = ModelRoot.Load(glbPath);
            foreach (Node node in model.LogicalNodes)
            {
                if (!TryGetObjectId(node, out ushort objectId))
                {
                    continue;
                }

                var local = node.LocalTransform;
                registry[objectId] = new SceneObjectTransform(
                    objectId,
                    local.Translation,
                    local.Rotation,
                    local.Scale);
            }
        }
        catch (Exception ex)
        {
            ConsoleLog.Warn($"Scene registry: failed to parse {glbPath}: {ex.Message}");
        }

        return registry;
    }

    private static bool TryGetObjectId(Node node, out ushort objectId)
    {
        objectId = 0;
        if (node.Extras is null)
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(node.Extras.ToJsonString());
            if (!doc.RootElement.TryGetProperty("objectId", out JsonElement idElement))
            {
                return false;
            }

            if (idElement.TryGetUInt16(out ushort id))
            {
                objectId = id;
                return true;
            }

            if (idElement.TryGetInt32(out int intId) && intId is >= 0 and <= ushort.MaxValue)
            {
                objectId = (ushort)intId;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}
