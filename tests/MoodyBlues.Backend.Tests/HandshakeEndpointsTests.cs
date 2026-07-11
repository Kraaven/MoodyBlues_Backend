using Microsoft.EntityFrameworkCore;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Handshake;

namespace MoodyBlues.Backend.Tests;

/// <summary>
/// Exercises <see cref="HandshakeEndpoints.HandleAsync"/> directly against an
/// EF Core InMemory-backed <see cref="Data.MoodyBluesDbContext"/> -- no live
/// Postgres or running host needed.
/// </summary>
public class HandshakeEndpointsTests
{
    private static readonly ServerConfig Config = new() { Host = "localhost", Port = 8765 };

    [Fact]
    public async Task NewScene_RequiresUpload_AndAutoProvisionsDeveloper()
    {
        using var db = TestDbContextFactory.Create();
        var sessions = new PendingSessionStore();
        var request = new HandshakeRequest("dev-1", "scene-1", "hash-abc", "session-1");

        var result = await HandshakeEndpoints.HandleAsync(request, db, Config, sessions);
        var response = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<HandshakeResponse>(result);

        Assert.NotNull(response);
        Assert.True(response!.SceneUploadRequired);
        Assert.NotNull(response.SceneUploadUrl);
        Assert.Contains("scene-1", response.SceneUploadUrl);
        Assert.Contains("developerId=dev-1", response.SceneUploadUrl);
        Assert.Contains("sceneHash=hash-abc", response.SceneUploadUrl);
        Assert.Equal("ws://localhost:8765/stream?session=session-1", response.WebSocketUrl);

        Assert.True(await db.Developers.AnyAsync(d => d.Id == "dev-1"));
        Assert.True(sessions.TryTake("session-1", out var pending));
        Assert.Equal("dev-1", pending!.DeveloperId);
        Assert.Equal("scene-1", pending.SceneId);
    }

    [Fact]
    public async Task ExistingScene_MatchingHash_DoesNotRequireUpload()
    {
        using var db = TestDbContextFactory.Create();
        db.Scenes.Add(new Scene { DeveloperId = "dev-1", SceneId = "scene-1", Hash = "hash-abc", GlbPath = "dev-1/scene-1.glb", UpdatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var sessions = new PendingSessionStore();
        var request = new HandshakeRequest("dev-1", "scene-1", "hash-abc", "session-2");

        var result = await HandshakeEndpoints.HandleAsync(request, db, Config, sessions);
        var response = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<HandshakeResponse>(result);

        Assert.NotNull(response);
        Assert.False(response!.SceneUploadRequired);
        Assert.Null(response.SceneUploadUrl);
    }

    [Fact]
    public async Task ExistingScene_MismatchedHash_RequiresUpload()
    {
        using var db = TestDbContextFactory.Create();
        db.Scenes.Add(new Scene { DeveloperId = "dev-1", SceneId = "scene-1", Hash = "old-hash", GlbPath = "dev-1/scene-1.glb", UpdatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var sessions = new PendingSessionStore();
        var request = new HandshakeRequest("dev-1", "scene-1", "new-hash", "session-3");

        var result = await HandshakeEndpoints.HandleAsync(request, db, Config, sessions);
        var response = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<HandshakeResponse>(result);

        Assert.NotNull(response);
        Assert.True(response!.SceneUploadRequired);
    }

    [Theory]
    [InlineData("", "scene-1", "hash", "session")]
    [InlineData("dev-1", "", "hash", "session")]
    [InlineData("dev-1", "scene-1", "", "session")]
    [InlineData("dev-1", "scene-1", "hash", "")]
    public async Task MissingRequiredField_ReturnsBadRequest(string developerId, string sceneId, string sceneHash, string sessionId)
    {
        using var db = TestDbContextFactory.Create();
        var sessions = new PendingSessionStore();
        var request = new HandshakeRequest(developerId, sceneId, sceneHash, sessionId);

        var result = await HandshakeEndpoints.HandleAsync(request, db, Config, sessions);
        var (statusCode, _) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(400, statusCode);
    }

    [Theory]
    [InlineData("../etc", "scene-1")]
    [InlineData("dev-1", "..\\windows")]
    [InlineData("dev/1", "scene-1")]
    public async Task UnsafeIdentifiers_ReturnBadRequest(string developerId, string sceneId)
    {
        using var db = TestDbContextFactory.Create();
        var sessions = new PendingSessionStore();
        var request = new HandshakeRequest(developerId, sceneId, "hash", "session");

        var result = await HandshakeEndpoints.HandleAsync(request, db, Config, sessions);
        var (statusCode, _) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(400, statusCode);
    }

    [Fact]
    public async Task RepeatedHandshake_SameSessionId_IsIdempotent()
    {
        using var db = TestDbContextFactory.Create();
        var sessions = new PendingSessionStore();
        var request = new HandshakeRequest("dev-1", "scene-1", "hash-abc", "session-retry");

        await HandshakeEndpoints.HandleAsync(request, db, Config, sessions);
        var secondResult = await HandshakeEndpoints.HandleAsync(request, db, Config, sessions);
        var (statusCode, _) = await HttpResultTestHelpers.ExecuteAsync(secondResult);

        Assert.Equal(200, statusCode);
        Assert.Equal(1, await db.Developers.CountAsync(d => d.Id == "dev-1"));
    }
}
