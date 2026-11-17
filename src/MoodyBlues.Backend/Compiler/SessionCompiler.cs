using MoodyBlues.Backend.Scenes;
using MoodyBlues.Backend.Storage;

namespace MoodyBlues.Backend.Compiler;

/// <summary>
/// Turns one session's raw log into a brotli-compressed replay file. Pure with respect to its inputs (the
/// raw log and the scene registry), so a session can be recompiled at any time with the same
/// result, or a better one after the compiler improves.
/// </summary>
public static class SessionCompiler
{
    /// <summary>Bump when the compiler's output changes meaningfully; stored on each session.</summary>
    public const int Version = 4;

    /// <summary>The compiled replay plus the uncompressed size of the file written for it.</summary>
    public sealed record Result(CompiledReplay Replay, long ReplayDecodedBytes);

    public static async Task<Result> CompileAsync(
        string rawLogFullPath,
        SceneRegistry registry,
        string replayFullPath,
        FitTolerances? tolerances = null,
        CancellationToken cancellationToken = default)
    {
        CompiledReplay replay = await Task.Run(
            () => new SessionReconstructor(registry, tolerances).Run(RawLogReader.Read(rawLogFullPath), cancellationToken),
            cancellationToken);

        long decodedBytes = 0;
        await StoragePaths.WriteAtomicAsync(
            replayFullPath, async s => decodedBytes = await ReplayFileWriter.WriteBrotliAsync(replay, s), cancellationToken);
        return new Result(replay, decodedBytes);
    }
}
