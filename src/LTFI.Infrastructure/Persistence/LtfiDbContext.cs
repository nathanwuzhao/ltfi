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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Project>(e =>
        {
            e.Property(p => p.Title).IsRequired();
            e.Property(p => p.Status).HasConversion<string>();
            // Progress is derived from task/subtask completion, not stored.
            e.Ignore(p => p.ProgressPercent);
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
