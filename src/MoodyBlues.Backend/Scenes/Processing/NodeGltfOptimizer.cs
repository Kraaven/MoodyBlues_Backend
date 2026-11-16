using System.Diagnostics;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Logging;

namespace MoodyBlues.Backend.Scenes.Processing;

/// <summary>
/// Runs <c>tools/optimize-scene/optimize-scene.mjs</c> (dependencies pinned in its package.json,
/// installed by the Dockerfile) to make an uploaded scene smaller for the dashboard: meshopt
/// geometry and KTX2 textures, matching the decoders the frontend registers in <c>loadScene.ts</c>.
///
/// The script leaves the node hierarchy, node transforms and every node's extras alone, because
/// replays address nodes through <c>extras.objectId</c> and animate them individually; see the
/// header of the script for what it deliberately does not do.
/// The process has a timeout so one pathological upload cannot stall the queue forever.
/// </summary>
public sealed class NodeGltfOptimizer(ServerConfig config) : IGltfOptimizer
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);

    public static IReadOnlyList<string> BuildArguments(string scriptPath, string inputPath, string outputPath) =>
        [scriptPath, inputPath, outputPath];

    public async Task<bool> OptimizeAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(inputPath))
        {
            ConsoleLog.Warn($"Scene processing: input file missing ({inputPath}).");
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // The GLB is untrusted input; cap the Node heap so one upload cannot take the whole host.
        startInfo.Environment["NODE_OPTIONS"] = $"--max-old-space-size={config.OptimizerMemoryLimitMb}";
        foreach (string arg in BuildArguments(config.OptimizerScript, inputPath, outputPath))
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
                ConsoleLog.Error($"Scene processing: scene optimizer timed out after {Timeout.TotalMinutes} min for {inputPath}.");
                return false;
            }
            catch (OperationCanceledException)
            {
                // Server shutting down: do not leave the optimizer running without its parent.
                process.Kill(entireProcessTree: true);
                throw;
            }

            if (process.ExitCode != 0)
            {
                ConsoleLog.Error(
                    $"Scene processing: scene optimizer exited {process.ExitCode} for {inputPath}."
                    + $"{Environment.NewLine}stdout: {await stdOut}{Environment.NewLine}stderr: {await stdErr}");
                return false;
            }

            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.Error($"Scene processing: failed to run the scene optimizer for {inputPath}.", ex);
            return false;
        }
    }
}
