using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace MoodyBlues.Backend.Scenes;

/// <summary>
/// Reads the Unity scene name out of an uploaded GLB: <c>scenes[scene ?? 0].name</c> in the glTF JSON
/// chunk. Only the 12-byte header and the first (JSON) chunk are read, and the chunk is size-bounded, so a
/// hostile or broken file costs little. Never throws: the name is a nicety and must not fail an upload.
/// </summary>
public static class GlbSceneName
{
    /// <summary>Longest JSON chunk read; larger ones are treated as unreadable rather than loaded into memory.</summary>
    public const int MaxJsonChunkBytes = 16 * 1024 * 1024;

    public const int MaxNameLength = 200;

    private const uint JsonChunkType = 0x4E4F534A; // "JSON"

    /// <summary>The trimmed name (at most <see cref="MaxNameLength"/> characters), or null when the file has none or is unreadable.</summary>
    public static string? TryReadFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TryRead(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? TryRead(Stream stream)
    {
        try
        {
            Span<byte> header = stackalloc byte[20]; // 12-byte GLB header + the first chunk's length and type
            if (!ReadExactly(stream, header))
            {
                return null;
            }

            if (!header[..4].SequenceEqual("glTF"u8))
            {
                return null;
            }

            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
            uint chunkType = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            if (chunkType != JsonChunkType || chunkLength == 0 || chunkLength > MaxJsonChunkBytes)
            {
                return null;
            }

            var json = new byte[chunkLength];
            if (!ReadExactly(stream, json))
            {
                return null;
            }

            return NameFrom(json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static string? NameFrom(byte[] json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("scenes", out JsonElement scenes) ||
            scenes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        int index = root.TryGetProperty("scene", out JsonElement sceneIndex) && sceneIndex.TryGetInt32(out int i) ? i : 0;
        if (index < 0 || index >= scenes.GetArrayLength())
        {
            return null;
        }

        JsonElement scene = scenes[index];
        if (scene.ValueKind != JsonValueKind.Object ||
            !scene.TryGetProperty("name", out JsonElement name) ||
            name.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? trimmed = name.GetString()?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
    }

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }
}
