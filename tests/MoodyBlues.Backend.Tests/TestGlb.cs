using System.Buffers.Binary;
using System.Text;

namespace MoodyBlues.Backend.Tests;

/// <summary>Builds tiny but structurally valid GLBs (header plus one JSON chunk) for scene-name tests.</summary>
public static class TestGlb
{
    /// <summary>A GLB whose glTF JSON is <paramref name="json"/>.</summary>
    public static byte[] FromJson(string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        int padded = (body.Length + 3) & ~3;
        var glb = new byte[12 + 8 + padded];
        "glTF"u8.CopyTo(glb);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8), (uint)glb.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12), (uint)padded);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16), 0x4E4F534A);
        body.CopyTo(glb.AsSpan(20));
        glb.AsSpan(20 + body.Length).Fill((byte)' ');
        return glb;
    }

    /// <summary>A GLB with one scene of the given name.</summary>
    public static byte[] WithSceneName(string name) =>
        FromJson($$"""{"asset":{"version":"2.0"},"scene":0,"scenes":[{"name":"{{name}}","nodes":[]}]}""");
}
