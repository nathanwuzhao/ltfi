using System;

namespace LTFI.Core.Domain;

/// <summary>A small checklist item under a <see cref="TaskItem"/> (plan §5).</summary>
public class SubtaskItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TaskItemId { get; set; }

    public TaskItem? TaskItem { get; set; }

    public string Title { get; set; } = string.Empty;

    public bool IsCompleted { get; set; }

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
