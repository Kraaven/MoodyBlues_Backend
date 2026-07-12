namespace MoodyBlues.Backend.Scenes.Processing;

/// <summary>
/// Runs the actual Draco/KTX2 optimization pass over one .glb file. Abstracted behind an interface
/// (rather than <see cref="SceneProcessingWorker"/> shelling out directly) so tests can substitute a
/// fake implementation instead of needing a real <c>gltf-transform</c>/<c>toktx</c> install on PATH.
/// </summary>
public interface IGltfOptimizer
{
    /// <summary>Returns true if <paramref name="outputPath"/> was successfully written; false on any failure (already logged).</summary>
    Task<bool> OptimizeAsync(string inputPath, string outputPath, CancellationToken cancellationToken);
}
