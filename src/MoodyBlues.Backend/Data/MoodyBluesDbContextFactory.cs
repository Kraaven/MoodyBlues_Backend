using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using MoodyBlues.Backend.Config;

namespace MoodyBlues.Backend.Data;

/// <summary>
/// Lets `dotnet ef migrations` construct a <see cref="MoodyBluesDbContext"/>
/// at design time, without spinning up the full ASP.NET Core host. Uses the
/// same environment-variable-driven config as the running server.
/// </summary>
public sealed class MoodyBluesDbContextFactory : IDesignTimeDbContextFactory<MoodyBluesDbContext>
{
    public MoodyBluesDbContext CreateDbContext(string[] args)
    {
        var config = ServerConfig.FromEnvironment();
        var options = new DbContextOptionsBuilder<MoodyBluesDbContext>()
            .UseNpgsql(config.DbConnectionString)
            .Options;
        return new MoodyBluesDbContext(options);
    }
}
