using Microsoft.EntityFrameworkCore;
using LTFI.Core.Domain;

namespace LTFI.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context backing LTFI's local SQLite store. Enum properties are
/// persisted as text for stable, human-readable migrations.
/// </summary>
public class LtfiDbContext(DbContextOptions<LtfiDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<SubtaskItem> Subtasks => Set<SubtaskItem>();
    public DbSet<TaskLabel> Labels => Set<TaskLabel>();
    public DbSet<FocusSession> FocusSessions => Set<FocusSession>();
    public DbSet<Milestone> Milestones => Set<Milestone>();
    public DbSet<EvidenceItem> Evidence => Set<EvidenceItem>();
    public DbSet<ReflectionEntry> Reflections => Set<ReflectionEntry>();
    public DbSet<ProjectArea> Areas => Set<ProjectArea>();
    public DbSet<OutboxCommand> Outbox => Set<OutboxCommand>();
    public DbSet<WeeklyCommitment> Commitments => Set<WeeklyCommitment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Project>(e =>
        {
            e.Property(p => p.Title).IsRequired();
            e.Property(p => p.Status).HasConversion<string>();
            // Derived, not stored.
            e.Ignore(p => p.ProgressPercent);
            e.Ignore(p => p.HasProgress);
            e.Ignore(p => p.IsArchived);
        });

        modelBuilder.Entity<TaskItem>(e =>
        {
            e.Property(t => t.Title).IsRequired();
            e.Property(t => t.Status).HasConversion<string>();
            e.Property(t => t.Priority).HasConversion<string>();

            // Deleting a project leaves its tasks behind as unassigned rather than deleting work.
            e.HasOne(t => t.Project)
                .WithMany(p => p.Tasks)
                .HasForeignKey(t => t.ProjectId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasMany(t => t.Subtasks)
                .WithOne(s => s.TaskItem)
                .HasForeignKey(s => s.TaskItemId)
                .OnDelete(DeleteBehavior.Cascade);

            // Summed from completed focus sessions at read time, not stored.
            e.Ignore(t => t.TimeSpent);
            e.Ignore(t => t.IsExternal);
            e.Ignore(t => t.IsPendingOnPhone);

            // Removing an area leaves its tasks in the project with no area.
            e.HasOne(t => t.Area)
                .WithMany(a => a.Tasks)
                .HasForeignKey(t => t.AreaId)
                .OnDelete(DeleteBehavior.SetNull);

            // One local row per external item. Native tasks have NULLs here, which SQLite's
            // unique index allows any number of.
            e.HasIndex(t => new { t.ExternalSource, t.ExternalId }).IsUnique();
        });

        modelBuilder.Entity<SubtaskItem>(e =>
        {
            e.Property(s => s.Title).IsRequired();
        });

        modelBuilder.Entity<Milestone>(e =>
        {
            e.Property(m => m.Title).IsRequired();
            e.Property(m => m.Status).HasConversion<string>();

            e.HasOne(m => m.Project)
                .WithMany(p => p.Milestones)
                .HasForeignKey(m => m.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectArea>(e =>
        {
            e.ToTable("ProjectAreas");
            e.Property(a => a.Name).IsRequired();

            e.HasOne(a => a.Project)
                .WithMany(p => p.Areas)
                .HasForeignKey(a => a.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OutboxCommand>(e =>
        {
            e.ToTable("OutboxCommands");
            e.Property(c => c.Op).IsRequired();
            e.Property(c => c.ExternalUrl).IsRequired();
            e.Property(c => c.PayloadJson).IsRequired();
            e.HasIndex(c => c.ExternalUrl);
        });

        modelBuilder.Entity<WeeklyCommitment>(e =>
        {
            e.ToTable("WeeklyCommitments");
            e.Property(c => c.Text).IsRequired();
            e.Property(c => c.Status).HasConversion<string>();
            e.HasIndex(c => c.CheckInId);
            e.HasIndex(c => c.WeekStart);

            // Deleting the check-in removes its commitments; deleting a linked task just unlinks.
            e.HasOne<ReflectionEntry>()
                .WithMany()
                .HasForeignKey(c => c.CheckInId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<TaskItem>()
                .WithMany()
                .HasForeignKey(c => c.LinkedTaskId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<TaskLabel>(e => e.Property(l => l.Name).IsRequired());

        modelBuilder.Entity<FocusSession>(e =>
        {
            e.Property(f => f.Status).HasConversion<string>();
            e.Property(f => f.Result).HasConversion<string>();
        });

        modelBuilder.Entity<EvidenceItem>(e =>
        {
            e.Property(ev => ev.Type).HasConversion<string>();
            e.Property(ev => ev.Title).IsRequired();
        });

        modelBuilder.Entity<ReflectionEntry>(e =>
        {
            e.Property(r => r.ScopeType).HasConversion<string>();
            e.Property(r => r.Body).IsRequired();
        });
    }
}
