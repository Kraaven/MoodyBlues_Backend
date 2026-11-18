using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Projects;
using MoodyBlues.Backend.Scenes;
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
        Assert.DoesNotContain("isPublic", body);
        Assert.DoesNotContain("shareToken", body);
        Assert.DoesNotContain("projectId", body);
        Assert.DoesNotContain(shared.ShareToken!, body);
        Assert.DoesNotContain(session.ProjectId.ToString(), body);

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
    public async Task SceneName_FallsBackToUnityName_ThenSceneId()
    {
        var (user, session, db) = await SeedAsync();
        SharingResponse shared = await SetAsync(user, session, db, true);
        Scene scene = (await db.Scenes.FindAsync(session.ProjectId, "scene1"))!;

        async Task<string> NameAsync() => JsonSerializer.Deserialize<SharedSessionResponse>(
            (await HttpResultTestHelpers.ExecuteAsync(await ShareEndpoints.GetAsync(shared.ShareToken!, db))).Body, Web)!.SceneName;

        Assert.Equal("scene1", await NameAsync());
        scene.UnityName = "PlaygroundScene";
        scene.DisplayName = "  ";
        await db.SaveChangesAsync();
        Assert.Equal("PlaygroundScene", await NameAsync());
        scene.DisplayName = "Level One";
        await db.SaveChangesAsync();
        Assert.Equal("Level One", await NameAsync());
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<(int Status, string Body)> PutDefaultsAsync(Guid user, Guid project, string? json, MoodyBluesDbContext db) =>
        await HttpResultTestHelpers.ExecuteAsync(await ProjectEndpoints.SetShareDefaultsAsync(
            project, new ShareSettingsRequest(json is null ? null : Json(json)), TestPrincipal.For(user), db));

    private static async Task<(int Status, string Body)> PutSettingsAsync(Guid user, Session s, string? json, MoodyBluesDbContext db) =>
        await HttpResultTestHelpers.ExecuteAsync(await SessionEndpoints.SetShareSettingsAsync(
            s.Id.ToString(), new ShareSettingsRequest(json is null ? null : Json(json)), TestPrincipal.For(user), db));

    private static async Task<SharedSessionResponse> ShareAsync(string token, MoodyBluesDbContext db) =>
        JsonSerializer.Deserialize<SharedSessionResponse>(
            (await HttpResultTestHelpers.ExecuteAsync(await ShareEndpoints.GetAsync(token, db))).Body, Web)!;

    [Fact]
    public async Task FirstPublic_CopiesTheProjectTemplate_AndLaterTemplateEditsDoNotChangeTheLink()
    {
        var (user, session, db) = await SeedAsync();
        Assert.Equal(200, (await PutDefaultsAsync(user, session.ProjectId, """{"version":1,"camera":{"fov":60}}""", db)).Status);

        SharingResponse shared = await SetAsync(user, session, db, true);
        Assert.Equal("""{"version":1,"camera":{"fov":60}}""", session.ShareSettingsJson);

        await PutDefaultsAsync(user, session.ProjectId, """{"version":1,"camera":{"fov":30}}""", db);
        SharedSessionResponse view = await ShareAsync(shared.ShareToken!, db);
        Assert.Equal(60, view.Settings!.Value.GetProperty("camera").GetProperty("fov").GetInt32());

        // Making it private and public again keeps the link's own settings.
        await SetAsync(user, session, db, false);
        await SetAsync(user, session, db, true);
        Assert.Equal(60, (await ShareAsync(shared.ShareToken!, db)).Settings!.Value.GetProperty("camera").GetProperty("fov").GetInt32());
    }

    [Fact]
    public async Task FirstPublic_WithNoTemplate_LeavesSettingsNull()
    {
        var (user, session, db) = await SeedAsync();
        SharingResponse shared = await SetAsync(user, session, db, true);
        Assert.Null(session.ShareSettingsJson);
        Assert.Null((await ShareAsync(shared.ShareToken!, db)).Settings);
    }

    [Fact]
    public async Task ShareSettings_FallBackFromTheLinkToTheTemplateToNull()
    {
        var (user, session, db) = await SeedAsync();
        SharingResponse shared = await SetAsync(user, session, db, true); // no template yet: link settings stay null
        Assert.Null((await ShareAsync(shared.ShareToken!, db)).Settings);

        await PutDefaultsAsync(user, session.ProjectId, """{"from":"template"}""", db);
        Assert.Equal("template", (await ShareAsync(shared.ShareToken!, db)).Settings!.Value.GetProperty("from").GetString());

        await PutSettingsAsync(user, session, """{"from":"link"}""", db);
        Assert.Equal("link", (await ShareAsync(shared.ShareToken!, db)).Settings!.Value.GetProperty("from").GetString());

        var (status, body) = await PutSettingsAsync(user, session, null, db);
        Assert.Equal(200, status);
        Assert.Equal("""{"settings":null}""", body);
        Assert.Equal("template", (await ShareAsync(shared.ShareToken!, db)).Settings!.Value.GetProperty("from").GetString());
    }

    [Fact]
    public async Task Settings_AreReturnedVerbatimAsJson_NotAnEscapedString()
    {
        var (user, session, db) = await SeedAsync();
        var (status, body) = await PutSettingsAsync(user, session, """{"version":1,"panels":{"logs":{"available":true}},"x":[1,2]}""", db);
        Assert.Equal(200, status);
        Assert.Equal("""{"settings":{"version":1,"panels":{"logs":{"available":true}},"x":[1,2]}}""", body);

        var (_, detail) = await HttpResultTestHelpers.ExecuteAsync(await SessionEndpoints.GetAsync(session.Id.ToString(), TestPrincipal.For(user), db));
        Assert.Contains("\"shareSettings\":{\"version\":1", detail);

        var (_, project) = await HttpResultTestHelpers.ExecuteAsync(await ProjectEndpoints.GetAsync(
            session.ProjectId, TestPrincipal.For(user), db, TestProjects.Config, TestProjects.Protector));
        Assert.Contains("\"shareDefaults\":null", project);
        await PutDefaultsAsync(user, session.ProjectId, """{"a":1}""", db);
        (_, project) = await HttpResultTestHelpers.ExecuteAsync(await ProjectEndpoints.GetAsync(
            session.ProjectId, TestPrincipal.For(user), db, TestProjects.Config, TestProjects.Protector));
        Assert.Contains("\"shareDefaults\":{\"a\":1}", project);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("true")]
    public async Task NonObjectSettings_AreRejected(string json)
    {
        var (user, session, db) = await SeedAsync();
        Assert.Equal(400, (await PutSettingsAsync(user, session, json, db)).Status);
        Assert.Equal(400, (await PutDefaultsAsync(user, session.ProjectId, json, db)).Status);
        Assert.Null(session.ShareSettingsJson);
    }

    [Fact]
    public async Task OversizeSettings_AreRejected_AndTheLimitItselfIsAccepted()
    {
        var (user, session, db) = await SeedAsync();
        string Body(int bytes) => "{\"k\":\"" + new string('x', bytes - 8) + "\"}";

        Assert.Equal(400, (await PutSettingsAsync(user, session, Body(ShareSettingsDocument.MaxBytes + 1), db)).Status);
        Assert.Equal(400, (await PutDefaultsAsync(user, session.ProjectId, Body(ShareSettingsDocument.MaxBytes + 1), db)).Status);
        Assert.Equal(200, (await PutSettingsAsync(user, session, Body(ShareSettingsDocument.MaxBytes), db)).Status);
    }

    [Fact]
    public async Task NonOwner_CannotChangeSettingsOrDefaults()
    {
        var (_, session, db) = await SeedAsync();
        Guid other = Guid.NewGuid();
        Assert.Equal(404, (await PutSettingsAsync(other, session, "{}", db)).Status);
        Assert.Equal(404, (await PutDefaultsAsync(other, session.ProjectId, "{}", db)).Status);
        Assert.Null(session.ShareSettingsJson);
    }

    [Fact]
    public async Task SettingsAndDefaultsChanges_AreAudited()
    {
        var (user, session, db) = await SeedAsync();
        await PutSettingsAsync(user, session, "{}", db);
        await PutDefaultsAsync(user, session.ProjectId, null, db);

        Assert.Contains(db.AuditEvents, e => e.Action == AuditLog.SessionShareSettings && e.Target == session.Id.ToString());
        Assert.Contains(db.AuditEvents, e => e.Action == AuditLog.ProjectShareDefaults && e.Target == session.ProjectId.ToString());
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
    public async Task Replay_NotReady_IsNotFound()
    {
        var (user, session, db) = await SeedAsync();
        SharingResponse shared = await SetAsync(user, session, db, true);

        var (notReady, _) = await HttpResultTestHelpers.ExecuteAsync(
            await ShareEndpoints.DownloadReplayAsync(shared.ShareToken!, db, _paths, new DefaultHttpContext()));
        Assert.Equal(404, notReady);
    }

    /// <summary>Makes the session Ready with a brotli replay file holding <paramref name="plain"/>.</summary>
    private async Task ReadyWithReplayAsync(Session session, MoodyBluesDbContext db, byte[] plain)
    {
        session.Status = SessionStatus.Ready;
        session.ReplayPath = StoragePaths.ReplayFile;
        string full = _paths.SessionFull(session.ReplayPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using (var file = File.Create(full))
        await using (var brotli = new BrotliStream(file, CompressionLevel.Fastest))
        {
            await brotli.WriteAsync(plain);
        }

        session.ReplaySizeBytes = new FileInfo(full).Length;
        session.ReplayDecodedSizeBytes = plain.Length;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Replay_WhenBrotliAccepted_IsServedAsStoredWithDecodedLength()
    {
        var (user, session, db) = await SeedAsync();
        SharingResponse shared = await SetAsync(user, session, db, true);
        byte[] plain = Enumerable.Range(0, 500).Select(i => (byte)(i % 7)).ToArray();
        await ReadyWithReplayAsync(session, db, plain);

        var http = new DefaultHttpContext();
        http.Request.Headers.AcceptEncoding = "gzip, br";
        var result = await ShareEndpoints.DownloadReplayAsync(shared.ShareToken!, db, _paths, http);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.PhysicalFileHttpResult>(result);
        Assert.Equal("br", http.Response.Headers.ContentEncoding.ToString());
        Assert.Equal("500", http.Response.Headers[SceneEndpoints.DecodedLengthHeader].ToString());
    }

    [Fact]
    public async Task Replay_WithoutBrotli_IsDecompressedOnTheFly()
    {
        var (user, session, db) = await SeedAsync();
        SharingResponse shared = await SetAsync(user, session, db, true);
        byte[] plain = Enumerable.Range(0, 500).Select(i => (byte)(i % 7)).ToArray();
        await ReadyWithReplayAsync(session, db, plain);

        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        http.Request.Headers.AcceptEncoding = "gzip";
        http.Response.Body = new MemoryStream();
        var result = await ShareEndpoints.DownloadReplayAsync(shared.ShareToken!, db, _paths, http);
        await result.ExecuteAsync(http);

        Assert.True(string.IsNullOrEmpty(http.Response.Headers.ContentEncoding.ToString()));
        Assert.Equal("500", http.Response.Headers[SceneEndpoints.DecodedLengthHeader].ToString());
        Assert.Equal(plain, ((MemoryStream)http.Response.Body).ToArray());
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
