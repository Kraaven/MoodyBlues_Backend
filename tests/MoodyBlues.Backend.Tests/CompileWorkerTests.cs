using System.Numerics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Compiler;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Ingest;
using MoodyBlues.Backend.Scenes;
using MoodyBlues.Backend.Storage;

namespace MoodyBlues.Backend.Tests;

/// <summary>The session state machine: Recording -> Queued -> Compiling -> Ready/Failed.</summary>
public class CompileWorkerTests : IDisposable
{
    private static readonly string Hash = new('c', 64);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mb-worker-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ServerConfig _config;
    private readonly StoragePaths _paths;
    private readonly ServiceProvider _services;
    private readonly ActiveConnections _connections = new();

    public CompileWorkerTests()
    {
        _config = new ServerConfig
        {
            ScenesDir = Path.Combine(_root, "scenes"),
            SessionsDir = Path.Combine(_root, "sessions"),
            ReconnectGracePeriod = TimeSpan.FromSeconds(60),
        };
        _paths = new StoragePaths(_config);
        string dbName = Guid.NewGuid().ToString();
        _services = new ServiceCollection()
            .AddDbContext<MoodyBluesDbContext>(o => o.UseInMemoryDatabase(dbName))
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private CompileWorker Worker() =>
        new(_services.GetRequiredService<IServiceScopeFactory>(), _config, _paths, _connections, new CompileWorkSignal());

    private MoodyBluesDbContext Db() => _services.CreateScope().ServiceProvider.GetRequiredService<MoodyBluesDbContext>();

    private async Task<Guid> SeedSessionAsync(SessionStatus status, DateTime lastActivity, bool writeLog = true)
    {
        using MoodyBluesDbContext db = Db();
        await DeveloperProvisioning.EnsureExistsAsync(db, "dev1");
        var id = Guid.NewGuid();
        string rawRelative = Path.Combine(StoragePaths.SessionDir("dev1", id), StoragePaths.RawLogFile);
        db.Sessions.Add(new Session
        {
            Id = id,
            DeveloperId = "dev1",
            SceneId = "scene1",
            SceneHash = Hash,
            ClientSessionId = id.ToString(),
            StreamTokenHash = id.ToString(),
            RawLogPath = rawRelative,
            Status = status,
            CreatedAtUtc = lastActivity,
            LastActivityUtc = lastActivity,
        });
        await db.SaveChangesAsync();

        if (writeLog)
        {
            using var writer = new RawLogWriter(_paths.SessionFull(rawRelative));
            writer.AppendConnectionOpened(0);
            writer.Append(RawLogRecordKind.Message, new WireBuilder().BeginSession().Time(1).DeltaPosition(1, new Vector3(0.1f, 0, 0)).EndSession().Build());
            writer.AppendConnectionClosed(RawLogCloseReason.ClientClosed);
        }

        return id;
    }

    private async Task SeedRegistryAsync()
    {
        using MoodyBluesDbContext db = Db();
        await IngestEndpointsTests.SeedRevisionAsync(db, "dev1", "scene1", Hash);
        SceneRevision revision = (await db.SceneRevisions.FindAsync("dev1", "scene1", Hash))!;
        string registryRelative = Path.Combine(StoragePaths.SceneRevisionDir("dev1", "scene1", Hash), StoragePaths.RegistryFile);
        await new SceneRegistry { Objects = [new SceneRegistryObject(1, 0, [0, 0, 0], [0, 0, 0, 1], [1, 1, 1], false, false, 1)] }
            .SaveAsync(_paths.SceneFull(registryRelative));
        revision.RegistryPath = registryRelative;
        revision.RegistryReady = true;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Seal_QueuesAbandonedSessions_ButNotOpenOrRecentOnes()
    {
        DateTime now = DateTime.UtcNow;
        Guid abandoned = await SeedSessionAsync(SessionStatus.Recording, now.AddMinutes(-5));
        Guid recent = await SeedSessionAsync(SessionStatus.Recording, now.AddSeconds(-5));
        Guid open = await SeedSessionAsync(SessionStatus.Recording, now.AddMinutes(-5));
        Guid empty = await SeedSessionAsync(SessionStatus.Recording, now.AddMinutes(-5), writeLog: false);
        _connections.Opened(open, new CancellationTokenSource());

        await Worker().SealAbandonedAsync(now, CancellationToken.None);

        using MoodyBluesDbContext db = Db();
        Assert.Equal(SessionStatus.Queued, (await db.Sessions.FindAsync(abandoned))!.Status);
        Assert.Equal(SessionStatus.Recording, (await db.Sessions.FindAsync(recent))!.Status);
        Assert.Equal(SessionStatus.Recording, (await db.Sessions.FindAsync(open))!.Status);
        Assert.Equal(SessionStatus.Failed, (await db.Sessions.FindAsync(empty))!.Status);
    }

    [Fact]
    public async Task Compile_WaitsForTheSceneRegistry_ThenProducesAReplay()
    {
        Guid id = await SeedSessionAsync(SessionStatus.Queued, DateTime.UtcNow);

        Assert.False(await Worker().CompileNextAsync(DateTime.UtcNow, CancellationToken.None));

        // Uploading the scene version moves its waiting sessions back to Queued.
        await SeedRegistryAsync();
        using (MoodyBluesDbContext requeue = Db())
        {
            (await requeue.Sessions.FindAsync(id))!.Status = SessionStatus.Queued;
            await requeue.SaveChangesAsync();
        }

        Assert.True(await Worker().CompileNextAsync(DateTime.UtcNow, CancellationToken.None));

        using MoodyBluesDbContext db = Db();
        Session session = (await db.Sessions.FindAsync(id))!;
        Assert.Equal(SessionStatus.Ready, session.Status);
        Assert.True(session.EndedCleanly);
        Assert.Equal(SessionCompiler.Version, session.CompilerVersion);
        Assert.True(File.Exists(_paths.SessionFull(session.ReplayPath!)));
        Assert.True(session.ReplaySizeBytes > 0);
        Assert.Equal(1, session.ObjectCount);
    }

    [Fact]
    public async Task Compile_SessionWithoutItsScene_WaitsInsteadOfFailing()
    {
        Guid id = await SeedSessionAsync(SessionStatus.Queued, DateTime.UtcNow.AddDays(-3));

        Assert.False(await Worker().CompileNextAsync(DateTime.UtcNow, CancellationToken.None));

        using MoodyBluesDbContext db = Db();
        Session session = (await db.Sessions.FindAsync(id))!;
        Assert.Equal(SessionStatus.WaitingForScene, session.Status);
        Assert.Null(session.ErrorMessage);
    }
}
