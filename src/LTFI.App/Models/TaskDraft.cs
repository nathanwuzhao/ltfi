using System;
using LTFI.Core.Domain;

namespace LTFI.Models;

public class TaskDraft
{
    public Guid? TaskId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public TaskPriority Priority { get; set; } = TaskPriority.Medium;

    public DateTime? DueDate { get; set; }

    public int RequiredWorkMinutes { get; set; }

    public int PointsValue { get; set; } = 5;

    public static TaskDraft CreateEmpty()
    {
        return new TaskDraft
        {
            DueDate = DateTime.Today
        };
    }

    public static TaskDraft FromTask(TaskItem task)
    {
        return new TaskDraft
        {
            TaskId = task.Id,
            Title = task.Title,
            Description = task.Description,
            Priority = task.Priority,
            DueDate = task.DueDate?.Date,
            RequiredWorkMinutes = task.RequiredWorkSeconds / 60,
            PointsValue = task.PointsValue
        };
    }
}
