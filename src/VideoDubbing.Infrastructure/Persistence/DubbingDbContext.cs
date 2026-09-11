using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VideoDubbing.Domain.Jobs;

namespace VideoDubbing.Infrastructure.Persistence;

public sealed class DubbingDbContext : DbContext
{
    public DubbingDbContext(DbContextOptions<DubbingDbContext> options) : base(options)
    {
    }

    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Speaker> Speakers => Set<Speaker>();
    public DbSet<TranscriptSegment> TranscriptSegments => Set<TranscriptSegment>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ProcessingLog> ProcessingLogs => Set<ProcessingLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Job>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Ignore(x => x.ModeKind);
            entity.Ignore(x => x.SubtitleStyleKind);
            entity.Property(x => x.OriginalFileName).HasMaxLength(512);
            entity.Property(x => x.ContentType).HasMaxLength(128);
            entity.Property(x => x.SourceLanguage).HasMaxLength(16);
            entity.Property(x => x.DetectedLanguage).HasMaxLength(16);
            entity.Property(x => x.StorageKey).HasMaxLength(1024);
            entity.Property(x => x.ErrorMessage).HasMaxLength(2000);
            entity.Property(x => x.CorrelationId).HasMaxLength(64);
            entity.Property(x => x.TargetLanguages)
                .HasConversion(
                    v => string.Join(',', v),
                    v => v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList());
            entity.Property(x => x.TargetLanguages)
                .Metadata.SetValueComparer(new ValueComparer<List<string>>(
                    (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()),
                    c => (c ?? new List<string>()).Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
                    c => (c ?? new List<string>()).ToList()));
            entity.HasMany(x => x.Speakers).WithOne(x => x.Job).HasForeignKey(x => x.JobId);
            entity.HasMany(x => x.Segments).WithOne(x => x.Job).HasForeignKey(x => x.JobId);
            entity.HasMany(x => x.Artifacts).WithOne(x => x.Job).HasForeignKey(x => x.JobId);
            entity.HasMany(x => x.AuditLogs).WithOne(x => x.Job).HasForeignKey(x => x.JobId);
            entity.HasMany(x => x.ProcessingLogs).WithOne(x => x.Job).HasForeignKey(x => x.JobId);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<Speaker>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Label).HasMaxLength(64);
            entity.Property(x => x.VoiceId).HasMaxLength(128);
        });

        modelBuilder.Entity<TranscriptSegment>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.SourceText).HasMaxLength(4000);
            entity.Property(x => x.Language).HasMaxLength(16);
            entity.HasOne(x => x.Speaker).WithMany().HasForeignKey(x => x.SpeakerId);
        });

        modelBuilder.Entity<Artifact>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Kind).HasMaxLength(64);
            entity.Property(x => x.Language).HasMaxLength(16);
            entity.Property(x => x.StorageKey).HasMaxLength(1024);
            entity.Property(x => x.ContentType).HasMaxLength(128);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Action).HasMaxLength(64);
            entity.Property(x => x.Details).HasMaxLength(2000);
        });

        modelBuilder.Entity<ProcessingLog>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Level).HasMaxLength(32);
            entity.Property(x => x.Stage).HasMaxLength(64);
            entity.Property(x => x.Message).HasMaxLength(2000);
        });
    }
}
