using System;
using System.Collections.Generic;
using System.Linq;
using LTFI.Core.Domain;
using LTFI.Models;

namespace LTFI.Services;

public class TaskService
{
    private readonly List<TaskItem> _tasks;

    public event EventHandler? TasksChanged;

    public TaskService()
    {
        _tasks = CreateSeedTasks();
    }

    public IReadOnlyList<TaskItem> GetTodayTasks() => _tasks;

    public IReadOnlyList<TaskItem> GetAllTasks() => _tasks;

    public TaskItem? GetTaskById(Guid taskId)
    {
        return _tasks.FirstOrDefault(task => task.Id == taskId);
    }

    public TaskItem CreateTask(TaskDraft draft)
    {
        ValidateDraft(draft);

        var task = new TaskItem
        {
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        ApplyDraft(task, draft);
        _tasks.Add(task);
        RaiseTasksChanged();

        return task;
    }

    public void UpdateTask(Guid taskId, TaskDraft draft)
    {
        ValidateDraft(draft);

        var task = FindTask(taskId);

        ApplyDraft(task, draft);

        if (task.Status == TaskStatus.Completed && !task.CanBeCompleted)
        {
            task.Status = TaskStatus.Paused;
            task.CompletedAt = null;
        }

        RaiseTasksChanged();
    }

    public void StartTask(Guid taskId)
    {
        var task = FindTask(taskId);

        if (!task.CanStartOrResume)
        {
            return;
        }

        task.Status = TaskStatus.InProgress;
        task.UpdatedAt = DateTime.Now;
        RaiseTasksChanged();
    }

    public void PauseTask(Guid taskId)
    {
        var task = FindTask(taskId);

        if (!task.CanPause)
        {
            return;
        }

        task.Status = TaskStatus.Paused;
        task.UpdatedAt = DateTime.Now;
        RaiseTasksChanged();
    }

    public void CompleteTask(Guid taskId)
    {
        var task = FindTask(taskId);

        if (!task.CanBeCompleted)
        {
            throw new InvalidOperationException("Task cannot be completed yet.");
        }

        task.Status = TaskStatus.Completed;
        task.CompletedAt = DateTime.Now;
        task.UpdatedAt = DateTime.Now;
        RaiseTasksChanged();
    }

    public void DeleteTask(Guid taskId)
    {
        var task = FindTask(taskId);
        _tasks.Remove(task);
        RaiseTasksChanged();
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
                AccumulatedWorkSeconds = 35 * 60,
                PointsValue = 50,
                DueDate = DateTime.Today
            },
            new TaskItem
            {
                Title = "Plan next app step",
                Description = "Write the next implementation target for the planner.",
                Priority = TaskPriority.Low,
                RequiredWorkSeconds = 0,
                PointsValue = 10,
                DueDate = DateTime.Today
            }
        ];
    }

    private TaskItem FindTask(Guid taskId)
    {
        return GetTaskById(taskId)
            ?? throw new InvalidOperationException("Task could not be found.");
    }

    private static void ValidateDraft(TaskDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Title))
        {
            throw new InvalidOperationException("Task title is required.");
        }

        if (draft.RequiredWorkMinutes < 0)
        {
            throw new InvalidOperationException("Required work time cannot be negative.");
        }

        if (draft.PointsValue < 0)
        {
            throw new InvalidOperationException("Points cannot be negative.");
        }
    }

    private static void ApplyDraft(TaskItem task, TaskDraft draft)
    {
        task.Title = draft.Title.Trim();
        task.Description = string.IsNullOrWhiteSpace(draft.Description)
            ? null
            : draft.Description.Trim();
        task.Priority = draft.Priority;
        task.DueDate = draft.DueDate?.Date;
        task.RequiredWorkSeconds = draft.RequiredWorkMinutes * 60;
        task.PointsValue = draft.PointsValue;
        task.UpdatedAt = DateTime.Now;
    }

    private void RaiseTasksChanged()
    {
        TasksChanged?.Invoke(this, EventArgs.Empty);
    }
}
