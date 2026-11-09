using Microsoft.EntityFrameworkCore;

namespace MoodyBlues.Backend.Data;

/// <summary>
/// EF Core context. Postgres holds metadata only; GLBs, raw logs and replay files live on disk
/// under ScenesDir / SessionsDir and are referenced by relative path.
/// </summary>
public sealed class MoodyBluesDbContext(DbContextOptions<MoodyBluesDbContext> options) : DbContext(options)
{
    public DbSet<Developer> Developers => Set<Developer>();

    public DbSet<Scene> Scenes => Set<Scene>();

    public DbSet<SceneRevision> SceneRevisions => Set<SceneRevision>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Developer>(entity => entity.HasKey(d => d.Id));

        modelBuilder.Entity<Scene>(entity =>
        {
            entity.HasKey(s => new { s.DeveloperId, s.SceneId });
            entity.HasOne<Developer>().WithMany().HasForeignKey(s => s.DeveloperId);
        });

        modelBuilder.Entity<SceneRevision>(entity =>
        {
            entity.HasKey(r => new { r.DeveloperId, r.SceneId, r.Hash });
            entity.HasOne<Scene>().WithMany().HasForeignKey(r => new { r.DeveloperId, r.SceneId });
            entity.HasIndex(r => r.Status);
            entity.HasIndex(r => new { r.DeveloperId, r.SceneId, r.Version }).IsUnique();
            entity.Ignore(r => r.ServableSizeBytes);
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
            entity.HasIndex(p => p.IngestKeyHash).IsUnique();
            entity.HasOne<User>().WithMany().HasForeignKey(p => p.UserId);
        });

        modelBuilder.Entity<Session>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => new { s.DeveloperId, s.SceneId });
            entity.HasIndex(s => new { s.DeveloperId, s.ClientSessionId }).IsUnique();
            entity.HasIndex(s => s.StreamTokenHash).IsUnique();
            entity.HasIndex(s => s.Status);

            // No FK to Scene/SceneRevision: the client streams and uploads concurrently, so a
            // session can exist before its scene revision does. The compile worker waits for it.
            entity.HasOne<Developer>().WithMany().HasForeignKey(s => s.DeveloperId);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => t.TokenHash).IsUnique();
            entity.HasIndex(t => t.FamilyId);
            entity.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.AtUtc);
            entity.Property(e => e.Action).HasMaxLength(64);
            entity.Property(e => e.Target).HasMaxLength(512);
            entity.Property(e => e.Detail).HasMaxLength(2048);
        });
    }
}
