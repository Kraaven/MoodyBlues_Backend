using MoodyBlues.Backend.Scenes;
using MoodyBlues.Backend.Storage;

namespace MoodyBlues.Backend.Compiler;

/// <summary>
/// Turns one session's raw log into a gzipped replay file. Pure with respect to its inputs (the
/// raw log and the scene registry), so a session can be recompiled at any time with the same
/// result, or a better one after the compiler improves.
/// </summary>
public static class SessionCompiler
{
    /// <summary>Bump when the compiler's output changes meaningfully; stored on each session.</summary>
    public const int Version = 1;

    public static async Task<CompiledReplay> CompileAsync(
        string rawLogFullPath,
        SceneRegistry registry,
        string replayFullPath,
        FitTolerances? tolerances = null,
        CancellationToken cancellationToken = default)
    {
        CompiledReplay replay = await Task.Run(
            () => new SessionReconstructor(registry, tolerances).Run(RawLogReader.Read(rawLogFullPath)),
            cancellationToken);

        await StoragePaths.WriteAtomicAsync(replayFullPath, s => ReplayFileWriter.WriteGzipAsync(replay, s), cancellationToken);
        return replay;
    }
}
