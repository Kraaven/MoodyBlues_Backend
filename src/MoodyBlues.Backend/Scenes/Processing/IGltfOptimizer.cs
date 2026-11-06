namespace MoodyBlues.Backend.Scenes.Processing;

/// <summary>Optimizes an uploaded GLB for web delivery. An interface so tests can substitute a fake.</summary>
public interface IGltfOptimizer
{
    /// <returns>True if <paramref name="outputPath"/> was written successfully.</returns>
    Task<bool> OptimizeAsync(string inputPath, string outputPath, CancellationToken cancellationToken);
}
