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
}

/// <summary>Input for creating or updating a <see cref="TaskItem"/>.</summary>
public sealed record TaskDraft
{
    public Guid? ProjectId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public TaskStatus Status { get; init; } = TaskStatus.Ready;
    public TaskPriority Priority { get; init; } = TaskPriority.Medium;
    public DateTimeOffset? DueAt { get; init; }
}
