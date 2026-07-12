using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Projects;
using MoodyBlues.Backend.Scenes;

namespace MoodyBlues.Backend.Tests;

/// <summary>
/// Exercises <see cref="SceneEndpoints"/> directly against an EF Core
/// InMemory-backed <see cref="MoodyBluesDbContext"/> -- no live Postgres or running host needed.
/// </summary>
public class SceneEndpointsTests : IDisposable
{
    private readonly string _scenesDir = Path.Combine(Path.GetTempPath(), "moodyblues-scene-endpoint-tests-" + Guid.NewGuid());

    private async Task<(Guid UserId, string DeveloperId, MoodyBluesDbContext Db)> SeedOwnedSceneAsync(
        byte[]? fileBytes,
        SceneProcessingStatus processingStatus = SceneProcessingStatus.Pending,
        byte[]? optimizedFileBytes = null)
    {
        var db = TestDbContextFactory.Create();
        Guid userId = Guid.NewGuid();

        var createResult = await ProjectEndpoints.CreateAsync(new CreateProjectRequest("Viewer Project"), TestPrincipal.For(userId), db);
        var created = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<ProjectSummaryResponse>(createResult);
        string developerId = created!.DeveloperId;

        string relativePath = Path.Combine(developerId, "scene-1.glb");
        string? optimizedRelativePath = optimizedFileBytes is not null ? Path.Combine(developerId, "scene-1.optimized.glb") : null;
        db.Scenes.Add(new Scene
        {
            DeveloperId = developerId,
            SceneId = "scene-1",
            Hash = "hash-1",
            RawGlbPath = relativePath,
            ProcessingStatus = processingStatus,
            OptimizedGlbPath = optimizedRelativePath,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        if (fileBytes is not null)
        {
            string fullPath = Path.Combine(_scenesDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllBytesAsync(fullPath, fileBytes);
        }

        if (optimizedFileBytes is not null)
        {
            string fullPath = Path.Combine(_scenesDir, optimizedRelativePath!);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllBytesAsync(fullPath, optimizedFileBytes);
        }

        return (userId, developerId, db);
    }

    [Fact]
    public async Task Rename_Owner_UpdatesDisplayName()
    {
        var (userId, developerId, db) = await SeedOwnedSceneAsync(fileBytes: null);

        var result = await SceneEndpoints.RenameAsync(developerId, "scene-1", new RenameSceneRequest("My Cool Scene"), TestPrincipal.For(userId), db);
        var response = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<SceneResponse>(result);

        Assert.NotNull(response);
        Assert.Equal("My Cool Scene", response!.DisplayName);

        Scene? scene = await db.Scenes.FindAsync(developerId, "scene-1");
        Assert.Equal("My Cool Scene", scene!.DisplayName);
    }

    [Fact]
    public async Task Rename_BlankName_ClearsDisplayName()
    {
        var (userId, developerId, db) = await SeedOwnedSceneAsync(fileBytes: null);
        await SceneEndpoints.RenameAsync(developerId, "scene-1", new RenameSceneRequest("Something"), TestPrincipal.For(userId), db);

        var result = await SceneEndpoints.RenameAsync(developerId, "scene-1", new RenameSceneRequest("   "), TestPrincipal.For(userId), db);
        var response = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<SceneResponse>(result);

        Assert.Null(response!.DisplayName);
    }

    [Fact]
    public async Task Rename_NonOwner_ReturnsNotFound()
    {
        var (_, developerId, db) = await SeedOwnedSceneAsync(fileBytes: null);

        var result = await SceneEndpoints.RenameAsync(developerId, "scene-1", new RenameSceneRequest("Hijacked"), TestPrincipal.For(Guid.NewGuid()), db);
        var (statusCode, _) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(404, statusCode);
    }

    [Fact]
    public async Task Rename_UnsafeIdentifiers_ReturnsBadRequest()
    {
        var (userId, _, db) = await SeedOwnedSceneAsync(fileBytes: null);

        var result = await SceneEndpoints.RenameAsync("../evil", "scene-1", new RenameSceneRequest("x"), TestPrincipal.For(userId), db);
        var (statusCode, _) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(400, statusCode);
    }

    [Fact]
    public async Task Download_Owner_ReturnsFile()
    {
        byte[] fakeGlb = [0x67, 0x6C, 0x54, 0x46];
        var (userId, developerId, db) = await SeedOwnedSceneAsync(fakeGlb);
        var config = new ServerConfig { ScenesDir = _scenesDir };

        var result = await SceneEndpoints.DownloadAsync(developerId, "scene-1", TestPrincipal.For(userId), db, config);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.PhysicalFileHttpResult>(result);
    }

    [Fact]
    public async Task Download_MissingFileOnDisk_ReturnsNotFound()
    {
        var (userId, developerId, db) = await SeedOwnedSceneAsync(fileBytes: null);
        var config = new ServerConfig { ScenesDir = _scenesDir };

        var result = await SceneEndpoints.DownloadAsync(developerId, "scene-1", TestPrincipal.For(userId), db, config);
        var (statusCode, _) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(404, statusCode);
    }

    [Fact]
    public async Task Download_NonOwner_ReturnsNotFound()
    {
        byte[] fakeGlb = [0x67, 0x6C, 0x54, 0x46];
        var (_, developerId, db) = await SeedOwnedSceneAsync(fakeGlb);
        var config = new ServerConfig { ScenesDir = _scenesDir };

        var result = await SceneEndpoints.DownloadAsync(developerId, "scene-1", TestPrincipal.For(Guid.NewGuid()), db, config);
        var (statusCode, _) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(404, statusCode);
    }

    [Fact]
    public async Task Download_NotYetOptimized_ServesRawFile()
    {
        byte[] rawGlb = [1, 2, 3];
        var (userId, developerId, db) = await SeedOwnedSceneAsync(rawGlb, SceneProcessingStatus.Pending);
        var config = new ServerConfig { ScenesDir = _scenesDir };

        var result = await SceneEndpoints.DownloadAsync(developerId, "scene-1", TestPrincipal.For(userId), db, config);
        var file = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.PhysicalFileHttpResult>(result);

        Assert.EndsWith("scene-1.glb", file.FileName);
        Assert.DoesNotContain("optimized", file.FileDownloadName);
    }

    [Fact]
    public async Task Download_Ready_ServesOptimizedFile()
    {
        byte[] rawGlb = [1, 2, 3];
        byte[] optimizedGlb = [9, 9];
        var (userId, developerId, db) = await SeedOwnedSceneAsync(rawGlb, SceneProcessingStatus.Ready, optimizedGlb);
        var config = new ServerConfig { ScenesDir = _scenesDir };

        var result = await SceneEndpoints.DownloadAsync(developerId, "scene-1", TestPrincipal.For(userId), db, config);
        var file = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.PhysicalFileHttpResult>(result);

        Assert.EndsWith("scene-1.optimized.glb", file.FileName);
    }

    [Fact]
    public async Task Download_ReadyButOptimizedFileMissing_FallsBackToRaw()
    {
        byte[] rawGlb = [1, 2, 3];
        // Status says Ready but the optimized file was never actually written to disk -- simulates a
        // stale/incomplete state; the raw upload must still be servable.
        var (userId, developerId, db) = await SeedOwnedSceneAsync(rawGlb, SceneProcessingStatus.Ready, optimizedFileBytes: null);
        Scene scene = (await db.Scenes.FindAsync(developerId, "scene-1"))!;
        scene.OptimizedGlbPath = Path.Combine(developerId, "scene-1.optimized.glb"); // path recorded, but file absent
        await db.SaveChangesAsync();
        var config = new ServerConfig { ScenesDir = _scenesDir };

        var result = await SceneEndpoints.DownloadAsync(developerId, "scene-1", TestPrincipal.For(userId), db, config);
        var file = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.PhysicalFileHttpResult>(result);

        Assert.EndsWith("scene-1.glb", file.FileName);
        Assert.DoesNotContain("optimized", file.FileDownloadName);
    }

    [Fact]
    public async Task Download_RelativeScenesDir_ReturnsFile()
    {
        // Regression test: production leaves ScenesDir at its relative default ("scenes"), unlike the
        // other tests above which use an absolute temp dir. Results.File(string) resolves a relative
        // path as "virtual" (against wwwroot, which doesn't exist here) instead of reading straight off
        // disk, so DownloadAsync must resolve to an absolute path itself -- this pins that behavior.
        string relativeScenesDir = "scenes-" + Guid.NewGuid();
        string absoluteScenesDir = Path.Combine(Directory.GetCurrentDirectory(), relativeScenesDir);
        try
        {
            byte[] fakeGlb = [0x67, 0x6C, 0x54, 0x46];
            var db = TestDbContextFactory.Create();
            Guid userId = Guid.NewGuid();
            var createResult = await ProjectEndpoints.CreateAsync(new CreateProjectRequest("Viewer Project"), TestPrincipal.For(userId), db);
            var created = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<ProjectSummaryResponse>(createResult);
            string developerId = created!.DeveloperId;

            string relativePath = Path.Combine(developerId, "scene-1.glb");
            db.Scenes.Add(new Scene
            {
                DeveloperId = developerId,
                SceneId = "scene-1",
                Hash = "hash-1",
                RawGlbPath = relativePath,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            string fullPath = Path.Combine(absoluteScenesDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllBytesAsync(fullPath, fakeGlb);

            var config = new ServerConfig { ScenesDir = relativeScenesDir };

            var result = await SceneEndpoints.DownloadAsync(developerId, "scene-1", TestPrincipal.For(userId), db, config);
            var file = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.PhysicalFileHttpResult>(result);

            Assert.True(Path.IsPathRooted(file.FileName));
            Assert.True(File.Exists(file.FileName));
        }
        finally
        {
            if (Directory.Exists(absoluteScenesDir))
            {
                Directory.Delete(absoluteScenesDir, recursive: true);
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_scenesDir))
        {
            Directory.Delete(_scenesDir, recursive: true);
        }
    }
}
