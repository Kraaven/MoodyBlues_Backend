using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Scenes;
using MoodyBlues.Backend.Scenes.Processing;

namespace MoodyBlues.Backend.Tests;

/// <summary>
/// Exercises <see cref="SceneUploadEndpoints.HandleAsync"/> directly (fake
/// <see cref="HttpRequest"/> + InMemory DbContext) -- no running host needed.
/// </summary>
public class SceneUploadEndpointsTests : IDisposable
{
    private readonly string _scenesDir = Path.Combine(Path.GetTempPath(), "moodyblues-tests-" + Guid.NewGuid());

    private static HttpRequest FakeRequest(byte[] body, string? developerId, string? sceneId, string? sceneHash)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(body);

        var query = new Dictionary<string, StringValues>();
        if (developerId is not null)
        {
            query["developerId"] = developerId;
        }

        if (sceneHash is not null)
        {
            query["sceneHash"] = sceneHash;
        }

        context.Request.Query = new QueryCollection(query);
        return context.Request;
    }

    [Fact]
    public async Task Upload_WritesFileAndUpsertsScene()
    {
        using var db = TestDbContextFactory.Create();
        var config = new ServerConfig { ScenesDir = _scenesDir };
        var queue = new SceneProcessingQueue();
        byte[] fakeGlb = [0x67, 0x6C, 0x54, 0x46]; // "glTF" magic, contents are otherwise irrelevant this milestone
        var request = FakeRequest(fakeGlb, "dev-1", null, "hash-1");

        var result = await SceneUploadEndpoints.HandleAsync("scene-1", request, db, config, queue);
        var (statusCode, _) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(200, statusCode);

        string expectedPath = Path.Combine(_scenesDir, "dev-1", "scene-1.glb");
        Assert.True(File.Exists(expectedPath));
        Assert.Equal(fakeGlb, await File.ReadAllBytesAsync(expectedPath));

        Scene? scene = await db.Scenes.FindAsync("dev-1", "scene-1");
        Assert.NotNull(scene);
        Assert.Equal("hash-1", scene!.Hash);
        Assert.Equal(fakeGlb.Length, scene!.RawSizeBytes);
        Assert.Equal(SceneProcessingStatus.Pending, scene.ProcessingStatus);
    }

    [Fact]
    public async Task Upload_EnqueuesProcessingJob()
    {
        using var db = TestDbContextFactory.Create();
        var config = new ServerConfig { ScenesDir = _scenesDir };
        var queue = new SceneProcessingQueue();
        var request = FakeRequest([1, 2, 3], "dev-1", null, "hash-1");

        await SceneUploadEndpoints.HandleAsync("scene-1", request, db, config, queue);

        Assert.True(queue.TryRead(out SceneProcessingJob? job));
        Assert.Equal("dev-1", job!.DeveloperId);
        Assert.Equal("scene-1", job.SceneId);
    }

    [Fact]
    public async Task ReUpload_OverwritesFileAndUpdatesHash()
    {
        using var db = TestDbContextFactory.Create();
        var config = new ServerConfig { ScenesDir = _scenesDir };
        var queue = new SceneProcessingQueue();

        await SceneUploadEndpoints.HandleAsync("scene-1", FakeRequest([1, 2, 3], "dev-1", null, "hash-1"), db, config, queue);
        await SceneUploadEndpoints.HandleAsync("scene-1", FakeRequest([4, 5, 6, 7], "dev-1", null, "hash-2"), db, config, queue);

        string path = Path.Combine(_scenesDir, "dev-1", "scene-1.glb");
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, await File.ReadAllBytesAsync(path));

        Scene? scene = await db.Scenes.FindAsync("dev-1", "scene-1");
        Assert.Equal("hash-2", scene!.Hash);
        Assert.Equal(4, scene.RawSizeBytes);
        Assert.Equal(1, await db.Scenes.CountAsync());
    }

    [Fact]
    public async Task ReUpload_ResetsProcessingStatusAndClearsOptimizedFile()
    {
        using var db = TestDbContextFactory.Create();
        var config = new ServerConfig { ScenesDir = _scenesDir };
        var queue = new SceneProcessingQueue();

        await SceneUploadEndpoints.HandleAsync("scene-1", FakeRequest([1, 2, 3], "dev-1", null, "hash-1"), db, config, queue);

        Scene scene = (await db.Scenes.FindAsync("dev-1", "scene-1"))!;
        scene.ProcessingStatus = SceneProcessingStatus.Ready;
        scene.OptimizedGlbPath = "dev-1/scene-1.optimized.glb";
        scene.OptimizedSizeBytes = 1;
        await db.SaveChangesAsync();

        await SceneUploadEndpoints.HandleAsync("scene-1", FakeRequest([4, 5, 6, 7], "dev-1", null, "hash-2"), db, config, queue);

        scene = (await db.Scenes.FindAsync("dev-1", "scene-1"))!;
        Assert.Equal(SceneProcessingStatus.Pending, scene.ProcessingStatus);
        Assert.Null(scene.OptimizedGlbPath);
        Assert.Null(scene.OptimizedSizeBytes);
    }

    [Fact]
    public async Task MissingQueryParams_ReturnsBadRequest()
    {
        using var db = TestDbContextFactory.Create();
        var config = new ServerConfig { ScenesDir = _scenesDir };
        var queue = new SceneProcessingQueue();
        var request = FakeRequest([1, 2, 3], developerId: null, sceneId: null, sceneHash: null);

        var result = await SceneUploadEndpoints.HandleAsync("scene-1", request, db, config, queue);
        var (statusCode, _) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(400, statusCode);
    }

    [Fact]
    public async Task PathTraversalDeveloperId_ReturnsBadRequest()
    {
        using var db = TestDbContextFactory.Create();
        var config = new ServerConfig { ScenesDir = _scenesDir };
        var queue = new SceneProcessingQueue();
        var request = FakeRequest([1, 2, 3], "../evil", null, "hash-1");

        var result = await SceneUploadEndpoints.HandleAsync("scene-1", request, db, config, queue);
        var (statusCode, _) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(400, statusCode);
        Assert.False(Directory.Exists(Path.Combine(_scenesDir, "..", "evil")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_scenesDir))
        {
            Directory.Delete(_scenesDir, recursive: true);
        }
    }
}
