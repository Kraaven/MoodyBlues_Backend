using Microsoft.EntityFrameworkCore;
using MoodyBlues.Backend.Data;

namespace MoodyBlues.Backend.Tests;

/// <summary>Builds a fresh EF Core InMemory-backed <see cref="MoodyBluesDbContext"/> per test -- no live Postgres needed.</summary>
public static class TestDbContextFactory
{
    public static MoodyBluesDbContext Create()
    {
        var options = new DbContextOptionsBuilder<MoodyBluesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new MoodyBluesDbContext(options);
    }
}
