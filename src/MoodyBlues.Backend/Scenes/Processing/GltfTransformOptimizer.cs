using System.Diagnostics;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Logging;

namespace MoodyBlues.Backend.Scenes.Processing;

/// <summary>
/// Runs <c>gltf-transform optimize</c> (version pinned in the Dockerfile) to make an uploaded scene
/// smaller for the dashboard: Draco geometry and KTX2 textures, matching the decoders the
/// frontend registers in <c>loadScene.ts</c>.
///
/// Every step that would change the node hierarchy is switched off, because replays address
/// nodes through <c>extras.objectId</c> and animate them individually:
/// <list type="bullet">
/// <item><c>--instance false</c>: GPU instancing moves meshes off their nodes into one static batch
///   (for any mesh used 5+ times) and deletes the emptied nodes.</item>
/// <item><c>--prune false</c>: pruning deletes empty leaf nodes, which tracked empty GameObjects are.</item>
/// <item><c>--flatten false</c>, <c>--join false</c>: both destroy the hierarchy.</item>
/// <item><c>--palette false</c>, <c>--simplify false</c>: keep materials and geometry as authored,
///   since the inspector shows them.</item>
/// </list>
/// The process has a timeout so one pathological upload cannot stall the queue forever.
/// </summary>
public sealed class GltfTransformOptimizer(ServerConfig config) : IGltfOptimizer
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);

    public static IReadOnlyList<string> BuildArguments(string inputPath, string outputPath) =>
    [
        "optimize", inputPath, outputPath,
        "--compress", "draco",
        "--texture-compress", "ktx2",
        "--texture-size", "2048",
        "--instance", "false",
        "--prune", "false",
        "--flatten", "false",
        "--join", "false",
        "--palette", "false",
        "--simplify", "false",
    ];

    public async Task<bool> OptimizeAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(inputPath))
        {
            ConsoleLog.Warn($"Scene processing: input file missing ({inputPath}).");
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = config.GltfTransformCommand,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in BuildArguments(inputPath, outputPath))
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();

            Task<string> stdOut = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stdErr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                ConsoleLog.Error($"Scene processing: gltf-transform timed out after {Timeout.TotalMinutes} min for {inputPath}.");
                return false;
            }

            if (process.ExitCode != 0)
            {
                ConsoleLog.Error(
                    $"Scene processing: gltf-transform exited {process.ExitCode} for {inputPath}."
                    + $"{Environment.NewLine}stdout: {await stdOut}{Environment.NewLine}stderr: {await stdErr}");
                return false;
            }

            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.Error($"Scene processing: failed to run gltf-transform for {inputPath}.", ex);
            return false;
        }
    }
}
