using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Scenes;

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
        byte[] fakeGlb = [0x67, 0x6C, 0x54, 0x46]; // "glTF" magic, contents are otherwise irrelevant this milestone
        var request = FakeRequest(fakeGlb, "dev-1", null, "hash-1");

        var result = await SceneUploadEndpoints.HandleAsync("scene-1", request, db, config);
        var (statusCode, _) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(200, statusCode);

        string expectedPath = Path.Combine(_scenesDir, "dev-1", "scene-1.glb");
        Assert.True(File.Exists(expectedPath));
        Assert.Equal(fakeGlb, await File.ReadAllBytesAsync(expectedPath));

        Scene? scene = await db.Scenes.FindAsync("dev-1", "scene-1");
        Assert.NotNull(scene);
        Assert.Equal("hash-1", scene!.Hash);
    }

    [Fact]
    public async Task ReUpload_OverwritesFileAndUpdatesHash()
    {
        using var db = TestDbContextFactory.Create();
        var config = new ServerConfig { ScenesDir = _scenesDir };

        await SceneUploadEndpoints.HandleAsync("scene-1", FakeRequest([1, 2, 3], "dev-1", null, "hash-1"), db, config);
        await SceneUploadEndpoints.HandleAsync("scene-1", FakeRequest([4, 5, 6, 7], "dev-1", null, "hash-2"), db, config);

        string path = Path.Combine(_scenesDir, "dev-1", "scene-1.glb");
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, await File.ReadAllBytesAsync(path));

        Scene? scene = await db.Scenes.FindAsync("dev-1", "scene-1");
        Assert.Equal("hash-2", scene!.Hash);
        Assert.Equal(1, await db.Scenes.CountAsync());
    }

    [Fact]
    public async Task MissingQueryParams_ReturnsBadRequest()
    {
        using var db = TestDbContextFactory.Create();
        var config = new ServerConfig { ScenesDir = _scenesDir };
        var request = FakeRequest([1, 2, 3], developerId: null, sceneId: null, sceneHash: null);

        var result = await SceneUploadEndpoints.HandleAsync("scene-1", request, db, config);
        var (statusCode, _) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(400, statusCode);
    }

    [Fact]
    public async Task PathTraversalDeveloperId_ReturnsBadRequest()
    {
        using var db = TestDbContextFactory.Create();
        var config = new ServerConfig { ScenesDir = _scenesDir };
        var request = FakeRequest([1, 2, 3], "../evil", null, "hash-1");

        var result = await SceneUploadEndpoints.HandleAsync("scene-1", request, db, config);
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
