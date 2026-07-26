using Microsoft.AspNetCore.Http;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Projects;
using MoodyBlues.Backend.Sessions;

namespace MoodyBlues.Backend.Tests;

/// <summary>
/// Exercises <see cref="SessionEndpoints"/> directly against an EF Core InMemory-backed
/// <see cref="MoodyBluesDbContext"/> -- no live Postgres or running host needed.
/// </summary>
public class SessionEndpointsTests : IDisposable
{
    private readonly string _sessionsDir = Path.Combine(Path.GetTempPath(), "moodyblues-session-endpoint-tests-" + Guid.NewGuid());

    private async Task<(Guid UserId, string DeveloperId, MoodyBluesDbContext Db, Data.Session Session)> SeedOwnedSessionAsync(
        SessionStatus status = SessionStatus.Ready,
        bool writeAnimationFile = true)
    {
        var db = TestDbContextFactory.Create();
        Guid userId = Guid.NewGuid();

        var createResult = await ProjectEndpoints.CreateAsync(new CreateProjectRequest("Session Test Project"), TestPrincipal.For(userId), db);
        var created = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<ProjectSummaryResponse>(createResult);
        string developerId = created!.DeveloperId;

        string relativePath = Path.Combine(developerId, "session-1.json.gz");
        var session = new Data.Session
        {
            Id = Guid.NewGuid(),
            DeveloperId = developerId,
            SceneId = "scene-1",
            Status = status,
            AnimationPath = status == SessionStatus.Ready ? relativePath : null,
            AnimationSizeBytes = 123,
            RawEventCount = 10,
            KeyframeCount = 4,
            DurationSeconds = 5.5,
            ObjectCount = 2,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        if (writeAnimationFile && status == SessionStatus.Ready)
        {
            string fullPath = Path.Combine(_sessionsDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllBytesAsync(fullPath, [1, 2, 3]);
        }

        return (userId, developerId, db, session);
    }

    [Fact]
    public async Task List_Owner_ReturnsSessionsForThatSceneOnly()
    {
        (Guid userId, string developerId, MoodyBluesDbContext db, Data.Session session) = await SeedOwnedSessionAsync();
        db.Sessions.Add(new Data.Session
        {
            Id = Guid.NewGuid(),
            DeveloperId = developerId,
            SceneId = "other-scene",
            Status = SessionStatus.Ready,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var result = await SessionEndpoints.ListAsync(developerId, "scene-1", TestPrincipal.For(userId), db);
        var sessions = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<List<SessionSummaryResponse>>(result);

        Assert.Single(sessions!);
        Assert.Equal(session.Id, sessions![0].Id);
    }

    [Fact]
    public async Task List_NonOwner_NotFound()
    {
        (Guid _, string developerId, MoodyBluesDbContext db, _) = await SeedOwnedSessionAsync();

        var result = await SessionEndpoints.ListAsync(developerId, "scene-1", TestPrincipal.For(Guid.NewGuid()), db);
        var (statusCode, _) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(404, statusCode);
    }

    [Fact]
    public async Task Get_Owner_ReturnsSessionDetail()
    {
        (Guid userId, string _, MoodyBluesDbContext db, Data.Session session) = await SeedOwnedSessionAsync();

        var result = await SessionEndpoints.GetAsync(session.Id.ToString(), TestPrincipal.For(userId), db);
        var detail = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<SessionDetailResponse>(result);

        Assert.Equal(session.Id, detail!.Id);
        Assert.Equal(session.DurationSeconds, detail.DurationSeconds);
    }

    [Fact]
    public async Task Get_NonOwner_NotFound()
    {
        (Guid _, string _, MoodyBluesDbContext db, Data.Session session) = await SeedOwnedSessionAsync();

        var result = await SessionEndpoints.GetAsync(session.Id.ToString(), TestPrincipal.For(Guid.NewGuid()), db);
        var (statusCode, _) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(404, statusCode);
    }

    [Fact]
    public async Task DownloadAnimation_NotReady_ReturnsNotFound()
    {
        (Guid userId, string _, MoodyBluesDbContext db, Data.Session session) = await SeedOwnedSessionAsync(SessionStatus.Processing);
        var config = new ServerConfig { SessionsDir = _sessionsDir };

        var result = await SessionEndpoints.DownloadAnimationAsync(
            session.Id.ToString(), TestPrincipal.For(userId), db, config, new DefaultHttpContext());
        var (statusCode, _) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(404, statusCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_sessionsDir))
        {
            Directory.Delete(_sessionsDir, recursive: true);
        }
    }
}
