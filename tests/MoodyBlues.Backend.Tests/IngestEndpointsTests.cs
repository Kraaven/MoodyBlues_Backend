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

        var result = await HandshakeEndpoints.HandleAsync(new HandshakeRequest("dev1", "scene1", Hash, "run-1"), db, _config);
        var response = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<HandshakeResponse>(result);

        Assert.True(response!.SceneUploadRequired);
        Assert.Equal($"http://localhost:8765/scenes/scene1?developerId=dev1&sceneHash={Hash}", response.SceneUploadUrl);
        Assert.StartsWith("ws://localhost:8765/stream?token=", response.WebSocketUrl);

        Session session = await db.Sessions.SingleAsync();
        Assert.Equal(SessionStatus.Recording, session.Status);
        string token = response.WebSocketUrl.Split("token=")[1];
        Assert.Equal(StreamTokens.Hash(token), session.StreamTokenHash);
        Assert.True(await db.Developers.AnyAsync(d => d.Id == "dev1"));
    }

    [Fact]
    public async Task Handshake_Retry_ReusesSession_AndRotatesToken()
    {
        using var db = TestDbContextFactory.Create();
        var request = new HandshakeRequest("dev1", "scene1", Hash, "run-1");

        var first = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<HandshakeResponse>(await HandshakeEndpoints.HandleAsync(request, db, _config));
        var second = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<HandshakeResponse>(await HandshakeEndpoints.HandleAsync(request, db, _config));

        Assert.Equal(1, await db.Sessions.CountAsync());
        Assert.NotEqual(first!.WebSocketUrl, second!.WebSocketUrl);
        Assert.Equal(StreamTokens.Hash(second.WebSocketUrl.Split("token=")[1]), (await db.Sessions.SingleAsync()).StreamTokenHash);
    }

    [Fact]
    public async Task Handshake_AfterSessionEnded_IsRefused()
    {
        using var db = TestDbContextFactory.Create();
        var request = new HandshakeRequest("dev1", "scene1", Hash, "run-1");
        await HandshakeEndpoints.HandleAsync(request, db, _config);
        (await db.Sessions.SingleAsync()).Status = SessionStatus.Ready;
        await db.SaveChangesAsync();

        var (status, _) = await HttpResultTestHelpers.ExecuteAsync(await HandshakeEndpoints.HandleAsync(request, db, _config));

        Assert.Equal(409, status);
    }

    [Fact]
    public async Task Handshake_KnownRevision_DoesNotAskForUpload()
    {
        using var db = TestDbContextFactory.Create();
        await SeedRevisionAsync(db, "dev1", "scene1", Hash);

        var response = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<HandshakeResponse>(
            await HandshakeEndpoints.HandleAsync(new HandshakeRequest("dev1", "scene1", Hash, "run-2"), db, _config));

        Assert.False(response!.SceneUploadRequired);
        Assert.Null(response.SceneUploadUrl);
    }

    [Theory]
    [InlineData("", "scene1", "run")]
    [InlineData("../dev", "scene1", "run")]
    [InlineData("dev1", "C:evil", "run")]
    [InlineData("dev1", "scene1", "run/1")]
    public async Task Handshake_InvalidIdentifiers_AreRejected(string developerId, string sceneId, string sessionId)
    {
        using var db = TestDbContextFactory.Create();
        var (status, _) = await HttpResultTestHelpers.ExecuteAsync(
            await HandshakeEndpoints.HandleAsync(new HandshakeRequest(developerId, sceneId, Hash, sessionId), db, _config));
        Assert.Equal(400, status);
    }

    [Fact]
    public async Task Handshake_InvalidHash_IsRejected()
    {
        using var db = TestDbContextFactory.Create();
        var (status, _) = await HttpResultTestHelpers.ExecuteAsync(
            await HandshakeEndpoints.HandleAsync(new HandshakeRequest("dev1", "scene1", "not-a-hash", "run"), db, _config));
        Assert.Equal(400, status);
    }

    [Fact]
    public async Task Upload_StoresAnImmutableRevision_PerHash()
    {
        using var db = TestDbContextFactory.Create();
        await DeveloperProvisioning.EnsureExistsAsync(db, "dev1");
        byte[] glb = FakeGlb(1000);

        var (status, _) = await HttpResultTestHelpers.ExecuteAsync(await UploadAsync(db, "dev1", "scene1", Hash, glb, gzip: true));
        Assert.Equal(200, status);
        await UploadAsync(db, "dev1", "scene1", OtherHash, FakeGlb(500), gzip: false);

        SceneRevision first = await db.SceneRevisions.FindAsync("dev1", "scene1", Hash) ?? throw new Exception("missing");
        Assert.Equal(glb.Length, first.RawSizeBytes);
        Assert.Equal(glb, await File.ReadAllBytesAsync(_paths.SceneFull(first.RawGlbPath)));
        Assert.Equal(2, await db.SceneRevisions.CountAsync());
        Assert.Equal(OtherHash, (await db.Scenes.SingleAsync()).LatestHash);
    }

    [Fact]
    public async Task Upload_SameHashTwice_KeepsTheFirstFile()
    {
        using var db = TestDbContextFactory.Create();
        await DeveloperProvisioning.EnsureExistsAsync(db, "dev1");
        byte[] original = FakeGlb(100);

        await UploadAsync(db, "dev1", "scene1", Hash, original, gzip: false);
        await UploadAsync(db, "dev1", "scene1", Hash, FakeGlb(200), gzip: false);

        SceneRevision revision = await db.SceneRevisions.SingleAsync();
        Assert.Equal(original, await File.ReadAllBytesAsync(_paths.SceneFull(revision.RawGlbPath)));
    }

    [Fact]
    public async Task Upload_NotAGlb_IsRejected_AndNothingIsStored()
    {
        using var db = TestDbContextFactory.Create();
        await DeveloperProvisioning.EnsureExistsAsync(db, "dev1");

        var (status, _) = await HttpResultTestHelpers.ExecuteAsync(await UploadAsync(db, "dev1", "scene1", Hash, [1, 2, 3, 4, 5], gzip: false));

        Assert.Equal(400, status);
        Assert.Empty(db.SceneRevisions);
        Assert.False(Directory.Exists(Path.Combine(_config.ScenesDir, "dev1", "scene1", Hash)) &&
                     Directory.EnumerateFiles(Path.Combine(_config.ScenesDir, "dev1", "scene1", Hash)).Any());
    }

    [Fact]
    public async Task Upload_TooLarge_IsRejected()
    {
        using var db = TestDbContextFactory.Create();
        await DeveloperProvisioning.EnsureExistsAsync(db, "dev1");
        var small = new ServerConfig { ScenesDir = _config.ScenesDir, MaxSceneUploadBytes = 1000 };

        IResult result = await SceneUploadEndpoints.HandleAsync("scene1", Request("dev1", Hash, FakeGlb(5000), gzip: false), db, small, _paths, new SceneWorkSignal());
        var (status, _) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(413, status);
        Assert.Empty(db.SceneRevisions);
    }

    [Fact]
    public async Task Upload_UnknownDeveloper_IsRejected()
    {
        using var db = TestDbContextFactory.Create();
        var (status, _) = await HttpResultTestHelpers.ExecuteAsync(await UploadAsync(db, "nobody", "scene1", Hash, FakeGlb(10), gzip: false));
        Assert.Equal(404, status);
    }

    internal static byte[] FakeGlb(int size)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        "glTF"u8.CopyTo(bytes);
        return bytes;
    }

    private Task<IResult> UploadAsync(MoodyBluesDbContext db, string developerId, string sceneId, string hash, byte[] body, bool gzip) =>
        SceneUploadEndpoints.HandleAsync(sceneId, Request(developerId, hash, body, gzip), db, _config, _paths, new SceneWorkSignal());

    private static HttpRequest Request(string developerId, string hash, byte[] body, bool gzip)
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
        context.Request.QueryString = new QueryString($"?developerId={developerId}&sceneHash={hash}");
        context.Request.Body = gzip ? new GZipStream(new MemoryStream(payload), CompressionMode.Decompress) : new MemoryStream(payload);
        return context.Request;
    }

    internal static async Task SeedRevisionAsync(MoodyBluesDbContext db, string developerId, string sceneId, string hash, string? rawPath = null)
    {
        await DeveloperProvisioning.EnsureExistsAsync(db, developerId);
        if (await db.Scenes.FindAsync(developerId, sceneId) is null)
        {
            db.Scenes.Add(new Scene { DeveloperId = developerId, SceneId = sceneId, LatestHash = hash, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow });
        }

        db.SceneRevisions.Add(new SceneRevision
        {
            DeveloperId = developerId,
            SceneId = sceneId,
            Hash = hash,
            RawGlbPath = rawPath ?? Path.Combine(developerId, sceneId, hash, StoragePaths.RawGlbFile),
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
