using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Projects;
using MoodyBlues.Backend.Scenes;
using MoodyBlues.Backend.Sessions;
using MoodyBlues.Backend.Storage;

namespace MoodyBlues.Backend.Tests;

/// <summary>Scene and session endpoints the dashboard uses, including ownership checks.</summary>
public class DashboardEndpointsTests : IDisposable
{
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mb-dashboard-tests-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;

    public DashboardEndpointsTests()
    {
        _paths = new StoragePaths(new ServerConfig { ScenesDir = Path.Combine(_root, "scenes"), SessionsDir = Path.Combine(_root, "sessions") });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task<(Guid User, string Dev, MoodyBluesDbContext Db)> SeedAsync()
    {
        var db = TestDbContextFactory.Create();
        Guid user = Guid.NewGuid();
        var created = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<ProjectSummaryResponse>(
            await ProjectEndpoints.CreateAsync(new CreateProjectRequest("Game"), TestPrincipal.For(user), db, TestProjects.Protector));
        string dev = created!.DeveloperId;

        foreach (string hash in new[] { HashA, HashB })
        {
            await IngestEndpointsTests.SeedRevisionAsync(db, dev, "scene1", hash);
            SceneRevision r = (await db.SceneRevisions.FindAsync(dev, "scene1", hash))!;
            string full = _paths.SceneFull(r.RawGlbPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, IngestEndpointsTests.FakeGlb(64));
        }

        Scene scene = (await db.Scenes.FindAsync(dev, "scene1"))!;
        scene.LatestHash = HashB;
        await db.SaveChangesAsync();
        return (user, dev, db);
    }

    [Fact]
    public async Task Download_DefaultsToLatestRevision_AndHonoursHash()
    {
        var (user, dev, db) = await SeedAsync();

        var latest = Assert.IsType<PhysicalFileHttpResult>(
            await SceneEndpoints.DownloadAsync(dev, "scene1", null, TestPrincipal.For(user), db, _paths, new DefaultHttpContext()));
        var older = Assert.IsType<PhysicalFileHttpResult>(
            await SceneEndpoints.DownloadAsync(dev, "scene1", HashA, TestPrincipal.For(user), db, _paths, new DefaultHttpContext()));

        Assert.Contains(HashB, latest.FileName);
        Assert.Contains(HashA, older.FileName);
        Assert.True(Path.IsPathRooted(latest.FileName));
    }

    [Fact]
    public async Task Download_NonOwner_GetsNotFound()
    {
        var (_, dev, db) = await SeedAsync();
        var (status, _) = await HttpResultTestHelpers.ExecuteAsync(
            await SceneEndpoints.DownloadAsync(dev, "scene1", null, TestPrincipal.For(Guid.NewGuid()), db, _paths, new DefaultHttpContext()));
        Assert.Equal(404, status);
    }

    [Fact]
    public async Task ProjectDetail_ListsSceneWithLatestRevisionInfo()
    {
        var (user, _, db) = await SeedAsync();
        Project project = db.Projects.Single();

        var detail = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<ProjectDetailResponse>(
            await ProjectEndpoints.GetAsync(project.Id, TestPrincipal.For(user), db, TestProjects.Config, TestProjects.Protector));

        SceneResponse scene = Assert.Single(detail!.Scenes);
        Assert.Equal(HashB, scene.LatestHash);
        Assert.Equal(2, scene.RevisionCount);
    }

    [Fact]
    public async Task Sessions_ReplayNotReady_IsNotFound_AndRecompileRequeues()
    {
        var (user, dev, db) = await SeedAsync();
        var session = new Session
        {
            Id = Guid.NewGuid(),
            DeveloperId = dev,
            SceneId = "scene1",
            SceneHash = HashA,
            ClientSessionId = "run",
            StreamTokenHash = "x",
            RawLogPath = "raw",
            Status = SessionStatus.Compiling,
            CreatedAtUtc = DateTime.UtcNow,
            LastActivityUtc = DateTime.UtcNow,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        var (replayStatus, _) = await HttpResultTestHelpers.ExecuteAsync(
            await SessionEndpoints.DownloadReplayAsync(session.Id.ToString(), TestPrincipal.For(user), db, _paths, new DefaultHttpContext()));
        Assert.Equal(404, replayStatus);

        var (conflict, _) = await HttpResultTestHelpers.ExecuteAsync(
            await SessionEndpoints.RecompileAsync(session.Id.ToString(), TestPrincipal.For(user), db, new CompileWorkSignal()));
        Assert.Equal(409, conflict);

        session.Status = SessionStatus.Failed;
        await db.SaveChangesAsync();
        var (accepted, _) = await HttpResultTestHelpers.ExecuteAsync(
            await SessionEndpoints.RecompileAsync(session.Id.ToString(), TestPrincipal.For(user), db, new CompileWorkSignal()));
        Assert.Equal(202, accepted);
        Assert.Equal(SessionStatus.Queued, session.Status);
    }

    [Fact]
    public async Task Sessions_OtherUser_GetsNotFound()
    {
        var (_, dev, db) = await SeedAsync();
        var (status, _) = await HttpResultTestHelpers.ExecuteAsync(
            await SessionEndpoints.ListAsync(dev, "scene1", null, null, TestPrincipal.For(Guid.NewGuid()), db));
        Assert.Equal(404, status);
    }
}
