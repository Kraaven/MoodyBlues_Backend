using Microsoft.EntityFrameworkCore;
using MoodyBlues.Backend.Common;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Projects;
using MoodyBlues.Backend.Scenes;

namespace MoodyBlues.Backend.Tests;

/// <summary>
/// Exercises <see cref="ProjectEndpoints"/> directly against an EF Core
/// InMemory-backed <see cref="MoodyBluesDbContext"/> -- no live Postgres or running host needed.
/// </summary>
public class ProjectEndpointsTests
{
    [Fact]
    public async Task Create_StoresTheProject_WithItsFirstIngestKey()
    {
        using var db = TestDbContextFactory.Create();
        Guid userId = Guid.NewGuid();

        var result = await ProjectEndpoints.CreateAsync(new CreateProjectRequest("My Game"), TestPrincipal.For(userId), db, TestProjects.Protector);
        var response = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<ProjectSummaryResponse>(result);

        Assert.NotNull(response);
        Assert.Equal("My Game", response!.Name);
        Assert.Equal(0, response.SceneCount);
        Assert.StartsWith(IngestAccess.KeyPrefix, response.IngestKeyPrefix);

        Project? project = await db.Projects.FindAsync(response.Id);
        Assert.NotNull(project);
        Assert.Equal(userId, project!.UserId);
        Assert.Equal(response.IngestKeyPrefix, project.IngestKeyPrefix);

        // The stored hash is the one the Unity client's key resolves through.
        string key = TestProjects.Protector.TryUnprotect(project.IngestKeyEncrypted, project.Id)!;
        Assert.Equal(Hashing.Sha256Hex(key), project.IngestKeyHash);
    }

    [Fact]
    public async Task Create_BlankName_ReturnsBadRequest()
    {
        using var db = TestDbContextFactory.Create();

        var result = await ProjectEndpoints.CreateAsync(new CreateProjectRequest("   "), TestPrincipal.For(Guid.NewGuid()), db, TestProjects.Protector);
        var (statusCode, _) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(400, statusCode);
    }

    [Fact]
    public async Task List_OnlyReturnsCallersOwnProjects_WithSceneCounts()
    {
        using var db = TestDbContextFactory.Create();
        Guid userA = Guid.NewGuid();
        Guid userB = Guid.NewGuid();

        await ProjectEndpoints.CreateAsync(new CreateProjectRequest("A's Project"), TestPrincipal.For(userA), db, TestProjects.Protector);
        await ProjectEndpoints.CreateAsync(new CreateProjectRequest("B's Project"), TestPrincipal.For(userB), db, TestProjects.Protector);

        var listResult = await ProjectEndpoints.ListAsync(TestPrincipal.For(userA), db);
        var projects = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<List<ProjectSummaryResponse>>(listResult);

        Assert.NotNull(projects);
        Assert.Single(projects!);
        Assert.Equal("A's Project", projects![0].Name);
    }

    [Fact]
    public async Task Get_IncludesScenesOfThatProject_WithConnectionString()
    {
        using var db = TestDbContextFactory.Create();
        Guid userId = Guid.NewGuid();

        var createResult = await ProjectEndpoints.CreateAsync(new CreateProjectRequest("Scened Project"), TestPrincipal.For(userId), db, TestProjects.Protector);
        var created = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<ProjectSummaryResponse>(createResult);

        db.Scenes.Add(new Scene
        {
            ProjectId = created!.Id,
            SceneId = "scene-1",
            LatestHash = new string('a', 64),
            DisplayName = "Renamed Scene",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        db.SceneRevisions.Add(new SceneRevision
        {
            ProjectId = created.Id,
            SceneId = "scene-1",
            Hash = new string('a', 64),
            RawGlbPath = "raw.glb",
            RawSizeBytes = 2048,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var getResult = await ProjectEndpoints.GetAsync(created.Id, TestPrincipal.For(userId), db, TestProjects.Config, TestProjects.Protector);
        var detail = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<ProjectDetailResponse>(getResult);

        Assert.NotNull(detail);
        Assert.Single(detail!.Scenes);
        Assert.Equal("scene-1", detail.Scenes[0].SceneId);
        Assert.Equal("Renamed Scene", detail.Scenes[0].DisplayName);
        Assert.Equal(2048, detail.Scenes[0].SizeBytes);
        Assert.Equal(SceneProcessingStatus.Pending, detail.Scenes[0].ProcessingStatus);
        Assert.StartsWith("http://" + IngestAccess.KeyPrefix, detail.ConnectionString);
    }

    [Fact]
    public async Task Get_KeyThatCannotBeDecrypted_HasNoConnectionString()
    {
        using var db = TestDbContextFactory.Create();
        Guid userId = Guid.NewGuid();
        var created = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<ProjectSummaryResponse>(
            await ProjectEndpoints.CreateAsync(new CreateProjectRequest("Moved secret"), TestPrincipal.For(userId), db, TestProjects.Protector));

        // The same database read by a server whose JWT secret (the encryption key's source) changed.
        var otherSecret = new IngestKeyProtector(new Config.ServerConfig { JwtSecret = new string('x', 40) });
        var detail = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<ProjectDetailResponse>(
            await ProjectEndpoints.GetAsync(created!.Id, TestPrincipal.For(userId), db, TestProjects.Config, otherSecret));

        Assert.Null(detail!.ConnectionString);
        Assert.Equal(created.IngestKeyPrefix, detail.IngestKeyPrefix);
    }

    [Fact]
    public async Task Get_SceneListBuiltFromOneRevisionQuery_PicksEachScenesOwnLatestRevision()
    {
        using var db = TestDbContextFactory.Create();
        Guid userId = Guid.NewGuid();
        var created = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<ProjectSummaryResponse>(
            await ProjectEndpoints.CreateAsync(new CreateProjectRequest("Two scenes"), TestPrincipal.For(userId), db, TestProjects.Protector));
        string oldHash = new('1', 64), newHash = new('2', 64);

        await IngestEndpointsTests.SeedRevisionAsync(db, created!.Id, "a", oldHash);
        await IngestEndpointsTests.SeedRevisionAsync(db, created.Id, "b", oldHash);
        db.SceneRevisions.Add(new SceneRevision { ProjectId = created.Id, SceneId = "a", Hash = newHash, Version = 2, RawGlbPath = "x", RawSizeBytes = 99, CreatedAtUtc = DateTime.UtcNow });
        (await db.Scenes.FindAsync(created.Id, "a"))!.LatestHash = newHash;
        await db.SaveChangesAsync();

        var detail = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<ProjectDetailResponse>(
            await ProjectEndpoints.GetAsync(created.Id, TestPrincipal.For(userId), db, TestProjects.Config, TestProjects.Protector));

        Assert.Equal(2, detail!.Scenes.Count);
        Assert.Equal(2, detail.Scenes.Single(x => x.SceneId == "a").CurrentVersion);
        Assert.Equal(99, detail.Scenes.Single(x => x.SceneId == "a").SizeBytes);
        Assert.Equal(0, detail.Scenes.Single(x => x.SceneId == "b").CurrentVersion);
    }

    [Fact]
    public async Task Get_OtherUsersProject_ReturnsNotFound()
    {
        using var db = TestDbContextFactory.Create();
        Guid owner = Guid.NewGuid();
        Guid intruder = Guid.NewGuid();

        var createResult = await ProjectEndpoints.CreateAsync(new CreateProjectRequest("Private Project"), TestPrincipal.For(owner), db, TestProjects.Protector);
        var created = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<ProjectSummaryResponse>(createResult);

        var getResult = await ProjectEndpoints.GetAsync(created!.Id, TestPrincipal.For(intruder), db, TestProjects.Config, TestProjects.Protector);
        var (statusCode, _) = await HttpResultTestHelpers.ExecuteAsync(getResult);

        Assert.Equal(404, statusCode);
    }
}
