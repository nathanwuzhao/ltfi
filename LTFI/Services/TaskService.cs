using System;
using System.Collections.Generic;
using LTFI.Models;

namespace LTFI.Services;

public class TaskService
{
    private readonly List<TaskItem> _tasks;

    public TaskService()
    {
        _tasks = CreateSeedTasks();
    }

    public IReadOnlyList<TaskItem> GetTodayTasks() => _tasks;

    public TaskItem AddTask(string title)
    {
        var task = new TaskItem
        {
            Title = title.Trim(),
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        _tasks.Add(task);
        return task;
    }

    public void CompleteTask(TaskItem task)
    {
        if (!task.CanBeCompleted)
        {
            throw new InvalidOperationException("Task cannot be completed yet.");
        }

        task.Status = TaskStatus.Completed;
        task.CompletedAt = DateTime.Now;
        task.UpdatedAt = DateTime.Now;
    }

    public void DeleteTask(TaskItem task)
    {
        _tasks.Remove(task);
    }

    private static List<TaskItem> CreateSeedTasks()
    {
        return
        [
            new TaskItem
            {
                Title = "Daily workout",
                Description = "Complete a short bodyweight workout.",
                Priority = TaskPriority.High,
                RequiredWorkSeconds = 10 * 60,
                AccumulatedWorkSeconds = 0,
                PointsValue = 5,
                DueDate = DateTime.Today
            },
            new TaskItem
            {
                Title = "Study SystemVerilog",
                Description = "Work through one focused study block.",
                Priority = TaskPriority.Medium,
                RequiredWorkSeconds = 3 * 60 * 60,
                PointsValue = 50,
                DueDate = DateTime.Today
            },
            new TaskItem
            {
                Title = "Plan next app step",
                Description = "Write the next implementation target for LTFI.",
                Priority = TaskPriority.Low,
                RequiredWorkSeconds = 0,
                PointsValue = 10,
                DueDate = DateTime.Today
            }
        ];
    }
}
