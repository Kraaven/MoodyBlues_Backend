using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Projects;
using MoodyBlues.Backend.Sessions;
using MoodyBlues.Backend.Storage;

namespace MoodyBlues.Backend.Tests;

/// <summary>Public share links: owner toggling and the anonymous share routes.</summary>
public class ShareEndpointsTests : IDisposable
{
    private static readonly string Hash = new('a', 64);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mb-share-tests-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;

    public ShareEndpointsTests()
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

    private async Task<(Guid User, Session Session, MoodyBluesDbContext Db)> SeedAsync(SessionStatus status = SessionStatus.Compiling)
    {
        var db = TestDbContextFactory.Create();
        Guid user = Guid.NewGuid();
        var created = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<ProjectSummaryResponse>(
            await ProjectEndpoints.CreateAsync(new CreateProjectRequest("Game"), TestPrincipal.For(user), db, TestProjects.Protector));
        Guid dev = created!.Id;

        await IngestEndpointsTests.SeedRevisionAsync(db, dev, "scene1", Hash);
        SceneRevision r = (await db.SceneRevisions.FindAsync(dev, "scene1", Hash))!;
        r.Version = 3;
        string full = _paths.SceneFull(r.RawGlbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, IngestEndpointsTests.FakeGlb(64));

        var session = new Session
        {
            Id = Guid.NewGuid(),
            ProjectId = dev,
            SceneId = "scene1",
            SceneHash = Hash,
            ClientSessionId = "run",
            StreamTokenHash = "x",
            RawLogPath = "raw",
            Status = status,
            CreatedAtUtc = DateTime.UtcNow,
            LastActivityUtc = DateTime.UtcNow,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        return (user, session, db);
    }

    private static async Task<SharingResponse> SetAsync(Guid user, Session s, MoodyBluesDbContext db, bool isPublic) =>
        (await HttpResultTestHelpers.ExecuteAndDeserializeAsync<SharingResponse>(
            await SessionEndpoints.SetSharingAsync(s.Id.ToString(), new SharingRequest(isPublic), TestPrincipal.For(user), db)))!;

    private static async Task<int> ShareStatusAsync(string token, MoodyBluesDbContext db) =>
        (await HttpResultTestHelpers.ExecuteAsync(await ShareEndpoints.GetAsync(token, db))).StatusCode;

    [Fact]
    public async Task MakingPublic_CreatesToken_AndShareReturnsPayload()
    {
        var (user, session, db) = await SeedAsync();
        Assert.Equal(404, await ShareStatusAsync(new string('A', 43), db));

        SharingResponse shared = await SetAsync(user, session, db, true);
        Assert.True(shared.IsPublic);
        Assert.True(ShareTokens.IsWellFormed(shared.ShareToken));

        var (status, body) = await HttpResultTestHelpers.ExecuteAsync(await ShareEndpoints.GetAsync(shared.ShareToken!, db));
        Assert.Equal(200, status);
        var payload = JsonSerializer.Deserialize<SharedSessionResponse>(body, Web)!;
        Assert.Equal(session.Id, payload.Session.Id);
        Assert.Equal("Game", payload.ProjectName);
        Assert.Equal("scene1", payload.SceneName);
        Assert.Equal(3, payload.SceneVersion);
        Assert.Contains("\"isPublic\":true", body);
        Assert.Contains("\"shareToken\"", body);

        var (_, detailBody) = await HttpResultTestHelpers.ExecuteAsync(await SessionEndpoints.GetAsync(session.Id.ToString(), TestPrincipal.For(user), db));
        Assert.Contains(shared.ShareToken!, detailBody);
    }

    [Fact]
    public async Task SceneName_UsesDisplayName_WhenSet()
    {
        var (user, session, db) = await SeedAsync();
        (await db.Scenes.FindAsync(session.ProjectId, "scene1"))!.DisplayName = "Level One";
        await db.SaveChangesAsync();

        SharingResponse shared = await SetAsync(user, session, db, true);
        var (_, body) = await HttpResultTestHelpers.ExecuteAsync(await ShareEndpoints.GetAsync(shared.ShareToken!, db));
        Assert.Equal("Level One", JsonSerializer.Deserialize<SharedSessionResponse>(body, Web)!.SceneName);
    }

    [Fact]
    public async Task MakingPrivate_StopsLink_ButKeepsToken_AndRepublishReusesIt()
    {
        var (user, session, db) = await SeedAsync();
        SharingResponse on = await SetAsync(user, session, db, true);

        SharingResponse off = await SetAsync(user, session, db, false);
        Assert.False(off.IsPublic);
        Assert.Equal(on.ShareToken, off.ShareToken);
        Assert.Equal(404, await ShareStatusAsync(on.ShareToken!, db));

        SharingResponse again = await SetAsync(user, session, db, true);
        Assert.Equal(on.ShareToken, again.ShareToken);
        Assert.Equal(200, await ShareStatusAsync(on.ShareToken!, db));
    }

    [Fact]
    public async Task Reset_InvalidatesOldToken_AndCreatesOneWhilePrivate()
    {
        var (user, session, db) = await SeedAsync();
        SharingResponse on = await SetAsync(user, session, db, true);

        var reset = (await HttpResultTestHelpers.ExecuteAndDeserializeAsync<SharingResponse>(
            await SessionEndpoints.ResetSharingAsync(session.Id.ToString(), TestPrincipal.For(user), db)))!;
        Assert.True(reset.IsPublic);
        Assert.NotEqual(on.ShareToken, reset.ShareToken);
        Assert.Equal(404, await ShareStatusAsync(on.ShareToken!, db));
        Assert.Equal(200, await ShareStatusAsync(reset.ShareToken!, db));

        var (user2, session2, db2) = await SeedAsync();
        var fresh = (await HttpResultTestHelpers.ExecuteAndDeserializeAsync<SharingResponse>(
            await SessionEndpoints.ResetSharingAsync(session2.Id.ToString(), TestPrincipal.For(user2), db2)))!;
        Assert.False(fresh.IsPublic);
        Assert.NotNull(fresh.ShareToken);
        Assert.Equal(404, await ShareStatusAsync(fresh.ShareToken!, db2));
    }

    [Fact]
    public async Task NonOwner_CannotToggleOrReset()
    {
        var (_, session, db) = await SeedAsync();
        var other = TestPrincipal.For(Guid.NewGuid());

        var (toggle, _) = await HttpResultTestHelpers.ExecuteAsync(
            await SessionEndpoints.SetSharingAsync(session.Id.ToString(), new SharingRequest(true), other, db));
        var (reset, _) = await HttpResultTestHelpers.ExecuteAsync(
            await SessionEndpoints.ResetSharingAsync(session.Id.ToString(), other, db));

        Assert.Equal(404, toggle);
        Assert.Equal(404, reset);
        Assert.False(session.IsPublic);
        Assert.Null(session.ShareToken);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+/A")]
    public async Task MalformedToken_IsNotFound_OnEveryRoute(string token)
    {
        var (_, _, db) = await SeedAsync();
        Assert.Equal(404, (await HttpResultTestHelpers.ExecuteAsync(await ShareEndpoints.GetAsync(token, db))).StatusCode);
        Assert.Equal(404, (await HttpResultTestHelpers.ExecuteAsync(await ShareEndpoints.DownloadReplayAsync(token, db, _paths, new DefaultHttpContext()))).StatusCode);
        Assert.Equal(404, (await HttpResultTestHelpers.ExecuteAsync(await ShareEndpoints.DownloadSceneAsync(token, db, _paths, new DefaultHttpContext()))).StatusCode);
    }

    [Fact]
    public async Task Replay_NotReady_IsNotFound_AndReadyIsServedGzipped()
    {
        var (user, session, db) = await SeedAsync();
        SharingResponse shared = await SetAsync(user, session, db, true);

        var (notReady, _) = await HttpResultTestHelpers.ExecuteAsync(
            await ShareEndpoints.DownloadReplayAsync(shared.ShareToken!, db, _paths, new DefaultHttpContext()));
        Assert.Equal(404, notReady);

        session.Status = SessionStatus.Ready;
        session.ReplayPath = "replay.mbr";
        string full = _paths.SessionFull(session.ReplayPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, [1, 2, 3]);
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext();
        var result = await ShareEndpoints.DownloadReplayAsync(shared.ShareToken!, db, _paths, http);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.PhysicalFileHttpResult>(result);
        Assert.Equal("gzip", http.Response.Headers.ContentEncoding.ToString());
    }

    [Fact]
    public async Task Scene_ServesSessionsRevision_AndIsNotFoundWhenNotUploaded()
    {
        var (user, session, db) = await SeedAsync();
        SharingResponse shared = await SetAsync(user, session, db, true);

        var file = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.PhysicalFileHttpResult>(
            await ShareEndpoints.DownloadSceneAsync(shared.ShareToken!, db, _paths, new DefaultHttpContext()));
        Assert.Equal("model/gltf-binary", file.ContentType);

        session.SceneHash = new string('b', 64);
        await db.SaveChangesAsync();
        Assert.Equal(404, (await HttpResultTestHelpers.ExecuteAsync(
            await ShareEndpoints.DownloadSceneAsync(shared.ShareToken!, db, _paths, new DefaultHttpContext()))).StatusCode);

        var (_, body) = await HttpResultTestHelpers.ExecuteAsync(await ShareEndpoints.GetAsync(shared.ShareToken!, db));
        Assert.Null(JsonSerializer.Deserialize<SharedSessionResponse>(body, Web)!.SceneVersion);
    }
}
