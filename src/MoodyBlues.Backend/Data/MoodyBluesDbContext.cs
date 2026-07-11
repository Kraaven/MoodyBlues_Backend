using Microsoft.EntityFrameworkCore;

namespace MoodyBlues.Backend.Data;

/// <summary>
/// EF Core context for the small amount of state that needs to survive a
/// restart: developer accounts (stubbed) and per-developer scene metadata.
/// Everything else (sessions, connection stats) is in-memory only.
/// </summary>
public sealed class MoodyBluesDbContext(DbContextOptions<MoodyBluesDbContext> options) : DbContext(options)
{
    public DbSet<Developer> Developers => Set<Developer>();

    public DbSet<Scene> Scenes => Set<Scene>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Project> Projects => Set<Project>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Developer>(entity =>
        {
            entity.HasKey(d => d.Id);
        });

        modelBuilder.Entity<Scene>(entity =>
        {
            entity.HasKey(s => new { s.DeveloperId, s.SceneId });
            entity.HasOne<Developer>()
                .WithMany()
                .HasForeignKey(s => s.DeveloperId);
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => p.DeveloperId).IsUnique();
            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(p => p.UserId);
        });
    }
}
