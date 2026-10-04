using System;
using LTFI.Core.Domain;

namespace LTFI.Core.Abstractions;

/// <summary>Input for creating or updating a <see cref="Project"/>.</summary>
public sealed record ProjectDraft
{
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public ProjectStatus Status { get; init; } = ProjectStatus.Active;
    public string? DoneCondition { get; init; }
    public DateTimeOffset? TargetDate { get; init; }

    /// <summary>A standing project (e.g. "Life") is exempt from the active-project limit.</summary>
    public bool IsStanding { get; init; }
}

/// <summary>Input for creating or updating a <see cref="TaskItem"/>.</summary>
public sealed record TaskDraft
{
    public Guid? ProjectId { get; init; }

    /// <summary>Area within <see cref="ProjectId"/>; required when the project is a standing one
    /// (the area name is the Reminders list the new reminder goes into).</summary>
    public Guid? AreaId { get; init; }

    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public TaskStatus Status { get; init; } = TaskStatus.Ready;
    public TaskPriority Priority { get; init; } = TaskPriority.Medium;
    public DateTimeOffset? DueAt { get; init; }

    /// <summary>Minimum focus minutes required before the task can be completed; null/0 = none.</summary>
    public int? RequiredMinutes { get; init; }
}
