using Microsoft.EntityFrameworkCore;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Projects;

namespace MoodyBlues.Backend.Tests;

/// <summary>
/// Exercises <see cref="ProjectEndpoints"/> directly against an EF Core
/// InMemory-backed <see cref="MoodyBluesDbContext"/> -- no live Postgres or running host needed.
/// </summary>
public class ProjectEndpointsTests
{
    [Fact]
    public async Task Create_GeneratesUniqueDeveloperId_AndProvisionsDeveloper()
    {
        using var db = TestDbContextFactory.Create();
        Guid userId = Guid.NewGuid();

        var result = await ProjectEndpoints.CreateAsync(new CreateProjectRequest("My Game"), TestPrincipal.For(userId), db);
        var response = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<ProjectSummaryResponse>(result);

        Assert.NotNull(response);
        Assert.Equal("My Game", response!.Name);
        Assert.False(string.IsNullOrWhiteSpace(response.DeveloperId));
        Assert.Equal(0, response.SceneCount);

        Assert.True(await db.Developers.AnyAsync(d => d.Id == response.DeveloperId));
        Project? project = await db.Projects.FindAsync(response.Id);
        Assert.NotNull(project);
        Assert.Equal(userId, project!.UserId);
    }

    [Fact]
    public async Task Create_BlankName_ReturnsBadRequest()
    {
        using var db = TestDbContextFactory.Create();

        var result = await ProjectEndpoints.CreateAsync(new CreateProjectRequest("   "), TestPrincipal.For(Guid.NewGuid()), db);
        var (statusCode, _) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(400, statusCode);
    }

    [Fact]
    public async Task List_OnlyReturnsCallersOwnProjects_WithSceneCounts()
    {
        using var db = TestDbContextFactory.Create();
        Guid userA = Guid.NewGuid();
        Guid userB = Guid.NewGuid();

        await ProjectEndpoints.CreateAsync(new CreateProjectRequest("A's Project"), TestPrincipal.For(userA), db);
        await ProjectEndpoints.CreateAsync(new CreateProjectRequest("B's Project"), TestPrincipal.For(userB), db);

        var listResult = await ProjectEndpoints.ListAsync(TestPrincipal.For(userA), db);
        var projects = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<List<ProjectSummaryResponse>>(listResult);

        Assert.NotNull(projects);
        Assert.Single(projects!);
        Assert.Equal("A's Project", projects![0].Name);
    }

    [Fact]
    public async Task Get_IncludesScenesForThatDeveloperId()
    {
        using var db = TestDbContextFactory.Create();
        Guid userId = Guid.NewGuid();

        var createResult = await ProjectEndpoints.CreateAsync(new CreateProjectRequest("Scened Project"), TestPrincipal.For(userId), db);
        var created = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<ProjectSummaryResponse>(createResult);

        db.Scenes.Add(new Scene
        {
            DeveloperId = created!.DeveloperId,
            SceneId = "scene-1",
            Hash = "hash",
            RawGlbPath = $"{created.DeveloperId}/scene-1.glb",
            RawSizeBytes = 2048,
            DisplayName = "Renamed Scene",
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var getResult = await ProjectEndpoints.GetAsync(created.Id, TestPrincipal.For(userId), db);
        var detail = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<ProjectDetailResponse>(getResult);

        Assert.NotNull(detail);
        Assert.Single(detail!.Scenes);
        Assert.Equal("scene-1", detail.Scenes[0].SceneId);
        Assert.Equal("Renamed Scene", detail.Scenes[0].DisplayName);
        Assert.Equal(2048, detail.Scenes[0].SizeBytes);
        Assert.Equal(SceneProcessingStatus.Pending, detail.Scenes[0].ProcessingStatus);
    }

    [Fact]
    public async Task Get_OtherUsersProject_ReturnsNotFound()
    {
        using var db = TestDbContextFactory.Create();
        Guid owner = Guid.NewGuid();
        Guid intruder = Guid.NewGuid();

        var createResult = await ProjectEndpoints.CreateAsync(new CreateProjectRequest("Private Project"), TestPrincipal.For(owner), db);
        var created = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<ProjectSummaryResponse>(createResult);

        var getResult = await ProjectEndpoints.GetAsync(created!.Id, TestPrincipal.For(intruder), db);
        var (statusCode, _) = await HttpResultTestHelpers.ExecuteAsync(getResult);

        Assert.Equal(404, statusCode);
    }
}
