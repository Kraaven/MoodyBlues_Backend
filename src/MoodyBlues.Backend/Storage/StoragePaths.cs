using MoodyBlues.Backend.Config;

namespace MoodyBlues.Backend.Storage;

/// <summary>
/// The one place that decides where files live on disk. Every path stored in the database is
/// relative (to ScenesDir or SessionsDir) so the data directories can be moved or remounted.
/// All inputs must already have passed <see cref="Common.PathSegments"/> validation.
/// </summary>
public sealed class StoragePaths(ServerConfig config)
{
    public const string RawGlbFile = "raw.glb";
    public const string OptimizedGlbFile = "optimized.glb";
    public const string RegistryFile = "registry.json";
    public const string RawLogFile = "raw.mblog";
    public const string ReplayFile = "replay.mbr.gz";

    public static string SceneRevisionDir(string developerId, string sceneId, string hash) =>
        Path.Combine(developerId, sceneId, hash);

    public static string SessionDir(string developerId, Guid sessionId) =>
        Path.Combine(developerId, sessionId.ToString("N"));

    public string SceneFull(string relativePath) => Path.GetFullPath(Path.Combine(config.ScenesDir, relativePath));

    public string SessionFull(string relativePath) => Path.GetFullPath(Path.Combine(config.SessionsDir, relativePath));

    /// <summary>
    /// Writes a file atomically: the content goes to a temporary sibling first and is moved into
    /// place only when complete, so readers never see a half-written file.
    /// </summary>
    public static async Task WriteAtomicAsync(string fullPath, Func<Stream, Task> write, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await write(stream);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temp, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
