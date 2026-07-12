using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Scenes.Processing;

namespace MoodyBlues.Backend.Tests;

/// <summary>Fake <see cref="IGltfOptimizer"/> so tests don't need a real gltf-transform/toktx install.</summary>
file sealed class FakeGltfOptimizer(bool succeed, byte[]? outputBytes = null) : IGltfOptimizer
{
    public List<(string Input, string Output)> Calls { get; } = [];

    public async Task<bool> OptimizeAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        Calls.Add((inputPath, outputPath));
        if (succeed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllBytesAsync(outputPath, outputBytes ?? [1, 2, 3], cancellationToken);
        }

        return succeed;
    }
}

/// <summary>
/// Exercises <see cref="SceneProcessingWorker.ProcessJobAsync"/> directly (fake <see cref="IGltfOptimizer"/>
/// + InMemory DbContext) -- no real gltf-transform/toktx install or running host needed.
/// </summary>
public class SceneProcessingWorkerTests : IDisposable
{
    private readonly string _scenesDir = Path.Combine(Path.GetTempPath(), "moodyblues-processing-tests-" + Guid.NewGuid());

    private async Task<Scene> SeedRawSceneAsync(MoodyBluesDbContext db, byte[] rawBytes)
    {
        string relativePath = Path.Combine("dev-1", "scene-1.glb");
        string fullPath = Path.Combine(_scenesDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, rawBytes);

        var scene = new Scene
        {
            DeveloperId = "dev-1",
            SceneId = "scene-1",
            Hash = "hash-1",
            RawGlbPath = relativePath,
            RawSizeBytes = rawBytes.Length,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        db.Scenes.Add(scene);
        await db.SaveChangesAsync();
        return scene;
    }

    [Fact]
    public async Task Success_MarksReadyAndRecordsOptimizedPathAndSize()
    {
        using var db = TestDbContextFactory.Create();
        await SeedRawSceneAsync(db, [1, 2, 3]);
        var config = new ServerConfig { ScenesDir = _scenesDir };
        var optimizer = new FakeGltfOptimizer(succeed: true, outputBytes: [9, 9]);
        var worker = new SceneProcessingWorker(new SceneProcessingQueue(), null!, config);

        await worker.ProcessJobAsync(new SceneProcessingJob("dev-1", "scene-1"), db, optimizer);

        Scene scene = (await db.Scenes.FindAsync("dev-1", "scene-1"))!;
        Assert.Equal(SceneProcessingStatus.Ready, scene.ProcessingStatus);
        Assert.Equal(Path.Combine("dev-1", "scene-1.optimized.glb"), scene.OptimizedGlbPath);
        Assert.Equal(2, scene.OptimizedSizeBytes);
        Assert.Single(optimizer.Calls);
    }

    [Fact]
    public async Task OptimizerFailure_MarksFailedAndLeavesRawServable()
    {
        using var db = TestDbContextFactory.Create();
        await SeedRawSceneAsync(db, [1, 2, 3]);
        var config = new ServerConfig { ScenesDir = _scenesDir };
        var optimizer = new FakeGltfOptimizer(succeed: false);
        var worker = new SceneProcessingWorker(new SceneProcessingQueue(), null!, config);

        await worker.ProcessJobAsync(new SceneProcessingJob("dev-1", "scene-1"), db, optimizer);

        Scene scene = (await db.Scenes.FindAsync("dev-1", "scene-1"))!;
        Assert.Equal(SceneProcessingStatus.Failed, scene.ProcessingStatus);
        Assert.Null(scene.OptimizedGlbPath);
        Assert.True(File.Exists(Path.Combine(_scenesDir, scene.RawGlbPath)));
    }

    [Fact]
    public async Task MissingScene_DoesNotThrow()
    {
        using var db = TestDbContextFactory.Create();
        var config = new ServerConfig { ScenesDir = _scenesDir };
        var optimizer = new FakeGltfOptimizer(succeed: true);
        var worker = new SceneProcessingWorker(new SceneProcessingQueue(), null!, config);

        await worker.ProcessJobAsync(new SceneProcessingJob("dev-missing", "scene-missing"), db, optimizer);

        Assert.Empty(optimizer.Calls);
    }

    public void Dispose()
    {
        if (Directory.Exists(_scenesDir))
        {
            Directory.Delete(_scenesDir, recursive: true);
        }
    }
}
