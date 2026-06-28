using System;
using System.Collections.Generic;

namespace LTFI.Core.Domain;

/// <summary>
/// A discrete piece of work, optionally belonging to a project (plan §2.1, §5).
/// Timer/points behaviour from the earlier draft was intentionally dropped here;
/// active-time tracking is reintroduced via <see cref="FocusSession"/> in Phase 2.
/// </summary>
public class TaskItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? ProjectId { get; set; }

    public Project? Project { get; set; }

    /// <summary>Optional milestone association (wired in Phase 3).</summary>
    public Guid? MilestoneId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public TaskStatus Status { get; set; } = TaskStatus.Ready;

    public TaskPriority Priority { get; set; } = TaskPriority.Medium;

    public DateTimeOffset? DueAt { get; set; }

    public DateTimeOffset? ScheduledStartAt { get; set; }

    public TimeSpan? EstimatedDuration { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public ICollection<SubtaskItem> Subtasks { get; set; } = new List<SubtaskItem>();

    /// <summary>
    /// Total focused time on this task, summed from its completed <see cref="FocusSession"/>s
    /// (not stored as a column — the read services populate it). Zero when none.
    /// </summary>
    public TimeSpan TimeSpent { get; set; }
}
