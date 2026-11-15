using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Compiler;
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

    private async Task<(Guid User, Guid Dev, MoodyBluesDbContext Db)> SeedAsync()
    {
        var db = TestDbContextFactory.Create();
        Guid user = Guid.NewGuid();
        var created = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<ProjectSummaryResponse>(
            await ProjectEndpoints.CreateAsync(new CreateProjectRequest("Game"), TestPrincipal.For(user), db, TestProjects.Protector));
        Guid dev = created!.Id;

        foreach (string hash in new[] { HashA, HashB })
        {
            await IngestEndpointsTests.SeedRevisionAsync(db, dev, "scene1", hash);
            SceneRevision r = (await db.SceneRevisions.FindAsync(dev, "scene1", hash))!;
            r.Version = hash == HashA ? 1 : 2;
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
        Assert.Equal(2, scene.CurrentVersion);
    }

    [Fact]
    public async Task Sessions_ReplayNotReady_IsNotFound_AndRecompileRequeues()
    {
        var (user, dev, db) = await SeedAsync();
        var session = new Session
        {
            Id = Guid.NewGuid(),
            ProjectId = dev,
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

    [Fact]
    public async Task SceneRoutes_AnotherProjectsId_GetsNotFound_EvenForTheSameOwner()
    {
        var (user, _, db) = await SeedAsync();
        var second = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<ProjectSummaryResponse>(
            await ProjectEndpoints.CreateAsync(new CreateProjectRequest("Other game"), TestPrincipal.For(user), db, TestProjects.Protector));

        // The caller owns the project, but the scene lives in the first one.
        var (status, _) = await HttpResultTestHelpers.ExecuteAsync(await SceneEndpoints.GetAsync(second!.Id, "scene1", TestPrincipal.For(user), db));
        Assert.Equal(404, status);
    }

    [Fact]
    public async Task SceneDetail_CarriesTheProjectId_AndDeleteByANonOwnerChangesNothing()
    {
        var (user, dev, db) = await SeedAsync();

        var detail = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<SceneDetailResponse>(
            await SceneEndpoints.GetAsync(dev, "scene1", TestPrincipal.For(user), db));
        Assert.Equal(dev, detail!.ProjectId);
        Assert.Equal(2, detail.Current!.Version);
        Assert.Equal(HashA, Assert.Single(detail.Archived).Hash);

        var (status, _) = await HttpResultTestHelpers.ExecuteAsync(await SceneEndpoints.DeleteSceneAsync(
            dev, "scene1", TestPrincipal.For(Guid.NewGuid()), db, _paths, new Ingest.ActiveConnections()));
        Assert.Equal(404, status);
        Assert.Equal(2, db.SceneRevisions.Count());
    }

    [Fact]
    public async Task Sessions_DetailAndList_CarryProjectIdWideningsAndActivity()
    {
        var (user, dev, db) = await SeedAsync();
        var activity = new SessionActivity([0.5, 1], [0.25], [], [0.1], [[0.4, 0.5]]);
        var session = new Session
        {
            Id = Guid.NewGuid(),
            ProjectId = dev,
            SceneId = "scene1",
            SceneHash = HashA,
            ClientSessionId = "run",
            StreamTokenHash = "x",
            RawLogPath = "raw",
            Status = SessionStatus.Ready,
            RotationWidenings = 9,
            ActivityJson = activity.ToJson(),
            CreatedAtUtc = DateTime.UtcNow,
            LastActivityUtc = DateTime.UtcNow,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        var detail = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<SessionDetailResponse>(
            await SessionEndpoints.GetAsync(session.Id.ToString(), TestPrincipal.For(user), db));
        Assert.Equal(dev, detail!.ProjectId);
        Assert.Equal(9, detail.RotationWidenings);
        Assert.Equal(activity.Bins, detail.Activity!.Bins);
        Assert.Equal([0.4, 0.5], Assert.Single(detail.Activity.Gaps));

        var (_, listBody) = await HttpResultTestHelpers.ExecuteAsync(
            await SessionEndpoints.ListAsync(dev, "scene1", null, null, TestPrincipal.For(user), db));
        var list = System.Text.Json.JsonSerializer.Deserialize<List<SessionSummaryResponse>>(
            listBody, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        SessionSummaryResponse summary = Assert.Single(list!);
        Assert.Equal(1, summary.SceneVersion);
        Assert.Equal(activity.Spawns, summary.Activity!.Spawns);
        Assert.Contains("\"bins\":[0.5,1]", listBody);
        Assert.DoesNotContain("rawEventCount", listBody);

        var (otherUser, _) = await HttpResultTestHelpers.ExecuteAsync(
            await SessionEndpoints.GetAsync(session.Id.ToString(), TestPrincipal.For(Guid.NewGuid()), db));
        Assert.Equal(404, otherUser);
    }

    [Fact]
    public async Task Sessions_NotReady_HaveNullActivity()
    {
        var (user, dev, db) = await SeedAsync();
        db.Sessions.Add(new Session
        {
            Id = Guid.NewGuid(), ProjectId = dev, SceneId = "scene1", SceneHash = HashA, ClientSessionId = "r",
            StreamTokenHash = "y", RawLogPath = "raw", CreatedAtUtc = DateTime.UtcNow, LastActivityUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var (_, body) = await HttpResultTestHelpers.ExecuteAsync(
            await SessionEndpoints.ListAsync(dev, "scene1", null, null, TestPrincipal.For(user), db));
        Assert.Contains("\"activity\":null", body);
    }
}
