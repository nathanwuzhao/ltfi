using System;

namespace LTFI.Models;

public class TaskItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public TaskPriority Priority { get; set; } = TaskPriority.Medium;

    public TaskStatus Status { get; set; } = TaskStatus.NotStarted;

    public DateTime? DueDate { get; set; }

    public DateTime? ScheduledStart { get; set; }

    public int RequiredWorkSeconds { get; set; }

    public int AccumulatedWorkSeconds { get; set; }

    public int PointsValue { get; set; } = 5;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public DateTime? CompletedAt { get; set; }

    public bool CanBeCompleted =>
        RequiredWorkSeconds <= 0 || AccumulatedWorkSeconds >= RequiredWorkSeconds;
}
