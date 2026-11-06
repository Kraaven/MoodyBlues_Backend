using System.Numerics;

namespace MoodyBlues.Backend.Compiler;

/// <summary>
/// Conversions between Unity space (left-handed, what the wire stream carries) and glTF space
/// (right-handed, what the scene GLB and the replay file use). They must match exactly what
/// UnityGLTF's exporter does, because the replay's transforms are applied to nodes of the GLB that
/// exporter produced:
/// <list type="bullet">
/// <item>position <c>(x, y, z) -&gt; (-x, y, z)</c> (<c>SchemaExtensions.CoordinateSpaceConversionScale</c>)</item>
/// <item>rotation <c>(x, y, z, w) -&gt; (x, -y, -z, w)</c> (<c>ToGltfQuaternionConvert</c>)</item>
/// <item>scale unchanged (<c>ToGltfVector3Raw</c>)</item>
/// <item>nodes with a Camera or Light also get their rotation multiplied by a 180 degree turn
///   about Y before conversion (<c>SetUnityTransform(..., invertLookDirection: true)</c>)</item>
/// </list>
/// The rotation map is conjugation by the X axis, so it preserves quaternion products: converting
/// the accumulated state at output time equals converting every delta, and the compiler can keep
/// mirroring the client's arithmetic in Unity space.
/// </summary>
public static class CoordinateSpace
{
    /// <summary>UnityGLTF's <c>InvertDirection</c>: <c>new Quaternion(0, -1, 0, 0)</c>.</summary>
    private static readonly Quaternion InvertDirection = new(0f, -1f, 0f, 0f);

    private static readonly Quaternion InvertDirectionInverse = new(0f, 1f, 0f, 0f);

    public static Vector3 PositionUnityToGltf(Vector3 p) => new(-p.X, p.Y, p.Z);

    public static Vector3 PositionGltfToUnity(Vector3 p) => new(-p.X, p.Y, p.Z);

    public static Quaternion RotationUnityToGltf(Quaternion q, bool invertLook)
    {
        Quaternion u = invertLook ? q * InvertDirection : q;
        return new Quaternion(u.X, -u.Y, -u.Z, u.W);
    }

    public static Quaternion RotationGltfToUnity(Quaternion g, bool invertLook)
    {
        var u = new Quaternion(g.X, -g.Y, -g.Z, g.W);
        return invertLook ? u * InvertDirectionInverse : u;
    }
}
