using System;
using System.Collections.Generic;

namespace LTFI.Core.Domain;

/// <summary>
/// A unit of sustained work. The central anti-sprawl entity (plan §3, §5).
/// Phase 1 keeps it intentionally small; lifecycle/limit mechanics arrive in Phase 3.
/// </summary>
public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public ProjectStatus Status { get; set; } = ProjectStatus.Active;

    /// <summary>The testable finish line for the project (plan §3.1). Optional in Phase 1.</summary>
    public string? DoneCondition { get; set; }

    public DateTimeOffset? TargetDate { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Last time the project saw activity; drives stalled-project detection later.</summary>
    public DateTimeOffset? LastActiveAt { get; set; }

    public ICollection<TaskItem> Tasks { get; set; } = new List<TaskItem>();

    public ICollection<Milestone> Milestones { get; set; } = new List<Milestone>();

    /// <summary>
    /// Derived from task/subtask completion (not stored). Requires <see cref="Tasks"/> (and their
    /// subtasks) to be loaded; the read services load them. Null when there are no countable tasks.
    /// </summary>
    public int? ProgressPercent => ProjectProgress.Calculate(Tasks);
}
