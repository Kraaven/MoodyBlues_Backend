using System.IO.Compression;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Ingest;
using MoodyBlues.Backend.Scenes;
using MoodyBlues.Backend.Storage;

namespace MoodyBlues.Backend.Tests;

/// <summary>Handshake and scene upload, the Unity-facing HTTP endpoints.</summary>
public class IngestEndpointsTests : IDisposable
{
    private static readonly string Hash = new('a', 64);
    private static readonly string OtherHash = new('b', 64);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mb-ingest-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _p = Guid.NewGuid();
    private readonly ServerConfig _config;
    private readonly StoragePaths _paths;

    public IngestEndpointsTests()
    {
        _config = new ServerConfig { ScenesDir = Path.Combine(_root, "scenes"), SessionsDir = Path.Combine(_root, "sessions") };
        _paths = new StoragePaths(_config);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Handshake_NewSession_CreatesRow_AndAsksForUpload()
    {
        using var db = TestDbContextFactory.Create();
        await SeedProjectAsync(db, _p);

        var result = await HandshakeEndpoints.HandleAsync(new HandshakeRequest("scene1", Hash, "run-1"), KeyedRequest(_p), db, _config);
        var response = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<HandshakeResponse>(result);

        Assert.True(response!.SceneUploadRequired);
        Assert.Equal($"http://localhost:8765/scenes/scene1?sceneHash={Hash}", response.SceneUploadUrl);
        Assert.Equal("ws://localhost:8765/stream", response.WebSocketUrl);

        Session session = await db.Sessions.SingleAsync();
        Assert.Equal(SessionStatus.Recording, session.Status);
        string token = response.StreamToken;
        Assert.Equal(Hashing.Sha256Hex(token), session.StreamTokenHash);
        Assert.Equal(_p, session.ProjectId);
    }

    [Fact]
    public async Task Handshake_Retry_ReusesSession_AndRotatesToken()
    {
        using var db = TestDbContextFactory.Create();
        await SeedProjectAsync(db, _p);
        var request = new HandshakeRequest("scene1", Hash, "run-1");

        var first = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<HandshakeResponse>(await HandshakeEndpoints.HandleAsync(request, KeyedRequest(_p), db, _config));
        var second = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<HandshakeResponse>(await HandshakeEndpoints.HandleAsync(request, KeyedRequest(_p), db, _config));

        Assert.Equal(1, await db.Sessions.CountAsync());
        Assert.NotEqual(first!.StreamToken, second!.StreamToken);
        Assert.Equal(Hashing.Sha256Hex(second.StreamToken), (await db.Sessions.SingleAsync()).StreamTokenHash);
    }

    [Fact]
    public async Task Handshake_AfterSessionEnded_IsRefused()
    {
        using var db = TestDbContextFactory.Create();
        await SeedProjectAsync(db, _p);
        var request = new HandshakeRequest("scene1", Hash, "run-1");
        await HandshakeEndpoints.HandleAsync(request, KeyedRequest(_p), db, _config);
        (await db.Sessions.SingleAsync()).Status = SessionStatus.Ready;
        await db.SaveChangesAsync();

        var (status, _) = await HttpResultTestHelpers.ExecuteAsync(await HandshakeEndpoints.HandleAsync(request, KeyedRequest(_p), db, _config));

        Assert.Equal(409, status);
    }

    [Fact]
    public async Task Handshake_KnownRevision_DoesNotAskForUpload()
    {
        using var db = TestDbContextFactory.Create();
        await SeedRevisionAsync(db, _p, "scene1", Hash);

        var response = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<HandshakeResponse>(
            await HandshakeEndpoints.HandleAsync(new HandshakeRequest("scene1", Hash, "run-2"), KeyedRequest(_p), db, _config));

        Assert.False(response!.SceneUploadRequired);
        Assert.Null(response.SceneUploadUrl);
    }

    [Fact]
    public async Task Handshake_SameClientSessionIdInTwoProjects_CreatesTwoSessions()
    {
        using var db = TestDbContextFactory.Create();
        Guid other = Guid.NewGuid();
        await SeedProjectAsync(db, _p);
        await SeedProjectAsync(db, other);
        var request = new HandshakeRequest("scene1", Hash, "run-1");

        await HandshakeEndpoints.HandleAsync(request, KeyedRequest(_p), db, _config);
        await HandshakeEndpoints.HandleAsync(request, KeyedRequest(other), db, _config);

        Assert.Equal(new[] { _p, other }.Order(), db.Sessions.Select(s => s.ProjectId).ToList().Order());
        Assert.Equal(Path.Combine(_p.ToString("N"), (await db.Sessions.FirstAsync(s => s.ProjectId == _p)).Id.ToString("N"), StoragePaths.RawLogFile),
                     (await db.Sessions.FirstAsync(s => s.ProjectId == _p)).RawLogPath);
    }

    [Fact]
    public async Task SceneCheck_ReportsWhetherTheVersionIsStored_PerProject()
    {
        using var db = TestDbContextFactory.Create();
        await SeedRevisionAsync(db, _p, "scene1", Hash);
        Guid other = Guid.NewGuid();
        await SeedProjectAsync(db, other);

        var known = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<SceneCheckResponse>(
            await SceneCheckEndpoints.HandleAsync(new SceneCheckRequest("scene1", Hash), KeyedRequest(_p), db, _config));
        var otherProject = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<SceneCheckResponse>(
            await SceneCheckEndpoints.HandleAsync(new SceneCheckRequest("scene1", Hash), KeyedRequest(other), db, _config));

        Assert.False(known!.SceneUploadRequired);
        Assert.Null(known.SceneUploadUrl);
        Assert.True(otherProject!.SceneUploadRequired);
        Assert.Equal($"http://localhost:8765/scenes/scene1?sceneHash={Hash}", otherProject.SceneUploadUrl);
    }

    [Theory]
    [InlineData("", "run")]
    [InlineData("../scene", "run")]
    [InlineData("C:evil", "run")]
    [InlineData("scene1", "run/1")]
    public async Task Handshake_InvalidIdentifiers_AreRejected(string sceneId, string sessionId)
    {
        using var db = TestDbContextFactory.Create();
        var (status, _) = await HttpResultTestHelpers.ExecuteAsync(
            await HandshakeEndpoints.HandleAsync(new HandshakeRequest(sceneId, Hash, sessionId), KeyedRequest(_p), db, _config));
        Assert.Equal(400, status);
    }

    [Fact]
    public async Task Handshake_InvalidHash_IsRejected()
    {
        using var db = TestDbContextFactory.Create();
        var (status, _) = await HttpResultTestHelpers.ExecuteAsync(
            await HandshakeEndpoints.HandleAsync(new HandshakeRequest("scene1", "not-a-hash", "run"), KeyedRequest(_p), db, _config));
        Assert.Equal(400, status);
    }

    [Fact]
    public async Task Upload_StoresAnImmutableRevision_PerHash()
    {
        using var db = TestDbContextFactory.Create();
        await SeedProjectAsync(db, _p);
        byte[] glb = FakeGlb(1000);

        var (status, _) = await HttpResultTestHelpers.ExecuteAsync(await UploadAsync(db, _p, "scene1", Hash, glb, gzip: true));
        Assert.Equal(200, status);
        await UploadAsync(db, _p, "scene1", OtherHash, FakeGlb(500), gzip: false);

        SceneRevision first = await db.SceneRevisions.FindAsync(_p, "scene1", Hash) ?? throw new Exception("missing");
        Assert.Equal(glb.Length, first.RawSizeBytes);
        Assert.Equal(glb, await File.ReadAllBytesAsync(_paths.SceneFull(first.RawGlbPath)));
        Assert.Equal(2, await db.SceneRevisions.CountAsync());
        Assert.Equal(OtherHash, (await db.Scenes.SingleAsync()).LatestHash);
    }

    [Fact]
    public async Task Upload_StoresTheUnityName_AndAnUnreadableGlbStillUploads()
    {
        using var db = TestDbContextFactory.Create();
        await SeedProjectAsync(db, _p);

        await UploadAsync(db, _p, "scene1", Hash, TestGlb.WithSceneName(" Playground "), gzip: false);
        Assert.Equal("Playground", (await db.Scenes.SingleAsync()).UnityName);

        // A GLB with no scene name is accepted and leaves the known name alone.
        var (status, _) = await HttpResultTestHelpers.ExecuteAsync(await UploadAsync(db, _p, "scene1", OtherHash, FakeGlb(300), gzip: false));
        Assert.Equal(200, status);
        Assert.Equal("Playground", (await db.Scenes.SingleAsync()).UnityName);

        // A new revision with a different name replaces it.
        await UploadAsync(db, _p, "scene1", new string('c', 64), TestGlb.WithSceneName("Renamed"), gzip: false);
        Assert.Equal("Renamed", (await db.Scenes.SingleAsync()).UnityName);
    }

    [Fact]
    public async Task Upload_SameHashTwice_KeepsTheFirstFile()
    {
        using var db = TestDbContextFactory.Create();
        await SeedProjectAsync(db, _p);
        byte[] original = FakeGlb(100);

        await UploadAsync(db, _p, "scene1", Hash, original, gzip: false);
        await UploadAsync(db, _p, "scene1", Hash, FakeGlb(200), gzip: false);

        SceneRevision revision = await db.SceneRevisions.SingleAsync();
        Assert.Equal(original, await File.ReadAllBytesAsync(_paths.SceneFull(revision.RawGlbPath)));
    }

    [Fact]
    public async Task Upload_NotAGlb_IsRejected_AndNothingIsStored()
    {
        using var db = TestDbContextFactory.Create();
        await SeedProjectAsync(db, _p);

        var (status, _) = await HttpResultTestHelpers.ExecuteAsync(await UploadAsync(db, _p, "scene1", Hash, [1, 2, 3, 4, 5], gzip: false));

        Assert.Equal(400, status);
        Assert.Empty(db.SceneRevisions);
        Assert.False(Directory.Exists(Path.Combine(_config.ScenesDir, _p.ToString("N"), "scene1", Hash)) &&
                     Directory.EnumerateFiles(Path.Combine(_config.ScenesDir, _p.ToString("N"), "scene1", Hash)).Any());
    }

    [Fact]
    public async Task Upload_TooLarge_IsRejected()
    {
        using var db = TestDbContextFactory.Create();
        await SeedProjectAsync(db, _p);
        var small = new ServerConfig { ScenesDir = _config.ScenesDir, MaxSceneUploadBytes = 1000 };

        IResult result = await SceneUploadEndpoints.HandleAsync("scene1", Request(_p, Hash, FakeGlb(5000), gzip: false), db, small, _paths, new SceneWorkSignal());
        var (status, _) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(413, status);
        Assert.Empty(db.SceneRevisions);
    }

    [Fact]
    public async Task Upload_UnknownKey_IsRejected()
    {
        using var db = TestDbContextFactory.Create();
        var (status, _) = await HttpResultTestHelpers.ExecuteAsync(await UploadAsync(db, Guid.NewGuid(), "scene1", Hash, FakeGlb(10), gzip: false));
        Assert.Equal(401, status);
    }

    /// <summary>The ingest key of the project seeded for <paramref name="projectId"/>. The key identifies the project.</summary>
    internal static string KeyFor(Guid projectId) => IngestAccess.KeyPrefix + "test-" + projectId.ToString("N");

    /// <summary>A request carrying the ingest key of <paramref name="projectId"/>'s seeded project.</summary>
    internal static HttpRequest KeyedRequest(Guid projectId)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[IngestAccess.HeaderName] = KeyFor(projectId);
        return context.Request;
    }

    internal static byte[] FakeGlb(int size)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        "glTF"u8.CopyTo(bytes);
        return bytes;
    }

    private Task<IResult> UploadAsync(MoodyBluesDbContext db, Guid projectId, string sceneId, string hash, byte[] body, bool gzip) =>
        SceneUploadEndpoints.HandleAsync(sceneId, Request(projectId, hash, body, gzip), db, _config, _paths, new SceneWorkSignal());

    private static HttpRequest Request(Guid projectId, string hash, byte[] body, bool gzip)
    {
        byte[] payload = body;
        if (gzip)
        {
            // The real pipeline's request-decompression middleware hands the endpoint the
            // decompressed stream; emulate that.
            using var ms = new MemoryStream();
            using (var z = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                z.Write(body);
            }

            payload = ms.ToArray();
        }

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?sceneHash={hash}");
        context.Request.Headers[IngestAccess.HeaderName] = KeyFor(projectId);
        context.Request.Body = gzip ? new GZipStream(new MemoryStream(payload), CompressionMode.Decompress) : new MemoryStream(payload);
        return context.Request;
    }

    /// <summary>Ingest endpoints only accept ingest keys that belong to a project; this creates that project (once).</summary>
    internal static async Task SeedProjectAsync(MoodyBluesDbContext db, Guid projectId)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId))
        {
            string key = KeyFor(projectId);
            db.Projects.Add(new Project
            {
                Id = projectId,
                UserId = Guid.NewGuid(),
                Name = "project",
                CreatedAtUtc = DateTime.UtcNow,
                IngestKeyHash = Hashing.Sha256Hex(key),
                IngestKeyEncrypted = TestProjects.Protector.Protect(key, projectId),
                IngestKeyPrefix = IngestAccess.DisplayPrefix(key),
                IngestKeyCreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
    }

    internal static async Task SeedRevisionAsync(MoodyBluesDbContext db, Guid projectId, string sceneId, string hash, string? rawPath = null)
    {
        await SeedProjectAsync(db, projectId);
        if (await db.Scenes.FindAsync(projectId, sceneId) is null)
        {
            db.Scenes.Add(new Scene { ProjectId = projectId, SceneId = sceneId, LatestHash = hash, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow });
        }

        db.SceneRevisions.Add(new SceneRevision
        {
            ProjectId = projectId,
            SceneId = sceneId,
            Hash = hash,
            RawGlbPath = rawPath ?? Path.Combine(projectId.ToString("N"), sceneId, hash, "raw.glb"),
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
