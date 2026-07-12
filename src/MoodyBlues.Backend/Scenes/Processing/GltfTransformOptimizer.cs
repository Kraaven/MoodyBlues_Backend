using System.Diagnostics;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Logging;

namespace MoodyBlues.Backend.Scenes.Processing;

/// <summary>
/// Shells out to <c>gltf-transform optimize</c> (see <see cref="ServerConfig.GltfTransformCommand"/>,
/// installed globally via npm in the Docker image -- see Dockerfile). Draco-compresses geometry and
/// converts textures to KTX2 (via KTX-Software's <c>toktx</c>, also installed in the image), matching
/// the DRACOLoader/KTX2Loader the frontend's <c>loadScene.ts</c> already wires up.
/// </summary>
public sealed class GltfTransformOptimizer(ServerConfig config) : IGltfOptimizer
{
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
        startInfo.ArgumentList.Add("optimize");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add("--compress");
        startInfo.ArgumentList.Add("draco");
        startInfo.ArgumentList.Add("--texture-compress");
        startInfo.ArgumentList.Add("ktx2");
        startInfo.ArgumentList.Add("--texture-size");
        startInfo.ArgumentList.Add("2048");

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();

            Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            string stdOut = await stdOutTask;
            string stdErr = await stdErrTask;

            if (process.ExitCode != 0)
            {
                ConsoleLog.Error(
                    $"Scene processing: gltf-transform exited {process.ExitCode} for {inputPath}."
                    + $"{Environment.NewLine}stdout: {stdOut}{Environment.NewLine}stderr: {stdErr}");
                return false;
            }

            ConsoleLog.Debug($"Scene processing: gltf-transform output for {inputPath}:{Environment.NewLine}{stdOut}");
            return true;
        }
        catch (Exception ex)
        {
            ConsoleLog.Error($"Scene processing: failed to launch gltf-transform for {inputPath}.", ex);
            return false;
        }
    }
}
