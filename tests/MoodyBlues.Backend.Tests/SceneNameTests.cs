using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Scenes;
using MoodyBlues.Backend.Storage;

namespace MoodyBlues.Backend.Tests;

/// <summary>The Unity scene name read from an uploaded GLB, and its startup backfill.</summary>
public class SceneNameTests : IDisposable
{
    private static readonly string Hash = new('a', 64);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mb-name-tests-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;

    public SceneNameTests()
    {
        _paths = new StoragePaths(new Config.ServerConfig { ScenesDir = Path.Combine(_root, "scenes"), SessionsDir = Path.Combine(_root, "sessions") });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string? Read(byte[] glb) => GlbSceneName.TryRead(new MemoryStream(glb));

    [Fact]
    public void ReadsTheDefaultScenesName_Trimmed()
    {
        Assert.Equal("PlaygroundScene", Read(TestGlb.WithSceneName("  PlaygroundScene ")));
    }

    [Fact]
    public void UsesTheSceneIndex_WhenPresent()
    {
        byte[] glb = TestGlb.FromJson("""{"scene":1,"scenes":[{"name":"First"},{"name":"Second"}]}""");
        Assert.Equal("Second", Read(glb));
    }

    [Fact]
    public void TruncatesLongNames()
    {
        Assert.Equal(GlbSceneName.MaxNameLength, Read(TestGlb.WithSceneName(new string('x', 500)))!.Length);
    }

    [Theory]
    [InlineData("""{"asset":{"version":"2.0"}}""")]
    [InlineData("""{"scenes":[]}""")]
    [InlineData("""{"scenes":[{"nodes":[]}]}""")]
    [InlineData("""{"scenes":[{"name":"   "}]}""")]
    [InlineData("""{"scene":5,"scenes":[{"name":"A"}]}""")]
    [InlineData("""{"scenes":[{"name":7}]}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""{ not json""")]
    public void NoUsableName_IsNull(string json)
    {
        Assert.Null(Read(TestGlb.FromJson(json)));
    }

    [Fact]
    public void MalformedGlbs_ReturnNullWithoutThrowing()
    {
        byte[] valid = TestGlb.WithSceneName("Name");
        Assert.Null(Read([]));
        Assert.Null(Read("glTF"u8.ToArray()));
        Assert.Null(Read(valid[..15])); // truncated inside the chunk header
        Assert.Null(Read(valid[..(valid.Length - 10)])); // truncated JSON chunk

        byte[] notGlb = (byte[])valid.Clone();
        notGlb[0] = (byte)'X';
        Assert.Null(Read(notGlb));

        byte[] wrongChunk = (byte[])valid.Clone();
        wrongChunk[16] = (byte)'B'; // first chunk is not JSON
        Assert.Null(Read(wrongChunk));

        byte[] huge = (byte[])valid.Clone();
        huge[15] = 0x7F; // declares a chunk far over the bound
        Assert.Null(Read(huge));

        Assert.Null(GlbSceneName.TryReadFile(Path.Combine(_root, "missing.glb")));
    }

    private async Task<(MoodyBluesDbContext Db, Guid Project)> SeedSceneAsync(string sceneId, byte[]? glb)
    {
        var db = TestDbContextFactory.Create();
        Guid project = Guid.NewGuid();
        await IngestEndpointsTests.SeedRevisionAsync(db, project, sceneId, Hash);
        if (glb is not null)
        {
            SceneRevision r = (await db.SceneRevisions.FindAsync(project, sceneId, Hash))!;
            string full = _paths.SceneFull(r.RawGlbPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, glb);
        }

        return (db, project);
    }

    [Fact]
    public async Task Backfill_NamesScenesFromTheirLatestGlb_AndIsIdempotent()
    {
        var (db, project) = await SeedSceneAsync("scene1", TestGlb.WithSceneName("Arena"));
        await IngestEndpointsTests.SeedRevisionAsync(db, project, "scene2", Hash); // no file on disk
        await IngestEndpointsTests.SeedRevisionAsync(db, project, "scene3", Hash);
        string broken = _paths.SceneFull((await db.SceneRevisions.FindAsync(project, "scene3", Hash))!.RawGlbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(broken)!);
        await File.WriteAllBytesAsync(broken, IngestEndpointsTests.FakeGlb(64));

        Assert.Equal(1, await UnityNameBackfillService.RunAsync(db, _paths, CancellationToken.None));
        Assert.Equal("Arena", (await db.Scenes.FindAsync(project, "scene1"))!.UnityName);
        Assert.Null((await db.Scenes.FindAsync(project, "scene2"))!.UnityName);
        Assert.Null((await db.Scenes.FindAsync(project, "scene3"))!.UnityName);

        Assert.Equal(0, await UnityNameBackfillService.RunAsync(db, _paths, CancellationToken.None));
        Assert.Equal("Arena", (await db.Scenes.FindAsync(project, "scene1"))!.UnityName);
    }

    [Fact]
    public async Task Backfill_DoesNotOverwriteAnExistingName()
    {
        var (db, project) = await SeedSceneAsync("scene1", TestGlb.WithSceneName("FromGlb"));
        (await db.Scenes.FindAsync(project, "scene1"))!.UnityName = "Existing";
        await db.SaveChangesAsync();

        Assert.Equal(0, await UnityNameBackfillService.RunAsync(db, _paths, CancellationToken.None));
        Assert.Equal("Existing", (await db.Scenes.FindAsync(project, "scene1"))!.UnityName);
    }
}
