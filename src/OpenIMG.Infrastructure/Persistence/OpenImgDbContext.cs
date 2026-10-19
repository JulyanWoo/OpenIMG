using Microsoft.EntityFrameworkCore;
using OpenIMG.Core.Entities;

namespace OpenIMG.Infrastructure.Persistence;

public class OpenImgDbContext : DbContext
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ImageAsset> ImageAssets => Set<ImageAsset>();
    public DbSet<ProcessingJob> ProcessingJobs => Set<ProcessingJob>();
    public DbSet<RestorationAttempt> RestorationAttempts => Set<RestorationAttempt>();
    public DbSet<ProviderUsageRecord> ProviderUsageRecords => Set<ProviderUsageRecord>();

    public OpenImgDbContext(DbContextOptions<OpenImgDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.SourceDirectory).IsRequired();
            entity.Property(e => e.OutputDirectory).IsRequired();

            entity.HasMany(e => e.ImageAssets)
                .WithOne(e => e.Project)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ImageAsset>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.OriginalFileName).IsRequired().HasMaxLength(260);
            entity.Property(e => e.OriginalFilePath).IsRequired();
            entity.Property(e => e.Sha256Hash).IsRequired().HasMaxLength(64);
            entity.HasIndex(e => new { e.ProjectId, e.Sha256Hash });

            entity.HasMany(e => e.ProcessingJobs)
                .WithOne(e => e.ImageAsset)
                .HasForeignKey(e => e.ImageAssetId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProcessingJob>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasMany(e => e.Attempts)
                .WithOne(e => e.ProcessingJob)
                .HasForeignKey(e => e.ProcessingJobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RestorationAttempt>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PromptVersion).HasMaxLength(50);
            entity.Property(e => e.GeneratedSha256).HasMaxLength(64);
        });

        modelBuilder.Entity<ProviderUsageRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).IsRequired().HasMaxLength(32);
            entity.Property(e => e.OperationId).IsRequired().HasMaxLength(160);
            entity.HasIndex(e => new { e.Provider, e.OperationId }).IsUnique();
            entity.HasIndex(e => new { e.Provider, e.StartedAt });
        });
    }
}
