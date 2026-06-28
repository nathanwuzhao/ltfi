using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using LTFI.Infrastructure.Persistence;
using TaskStatus = LTFI.Core.Domain.TaskStatus;

namespace LTFI.Infrastructure.Services;

/// <summary>Persistence-backed <see cref="ITaskService"/> including subtask management.</summary>
public sealed class TaskService(IDbContextFactory<LtfiDbContext> contextFactory) : ITaskService
{
    private readonly IDbContextFactory<LtfiDbContext> _contextFactory = contextFactory;

    public async Task<IReadOnlyList<TaskItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        // SQLite can't ORDER BY a DateTimeOffset column, so order newest-first in memory.
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var tasks = await db.Tasks
            .AsNoTracking()
            .Include(t => t.Subtasks)
            .ToListAsync(cancellationToken);

        await PopulateTimeSpentAsync(db, tasks, cancellationToken);

        return tasks
            .OrderByDescending(t => t.CreatedAt)
            .ToList();
    }

    public async Task<IReadOnlyList<TaskItem>> GetTodayAsync(CancellationToken cancellationToken = default)
    {
        // Due today or earlier, and still open. SQLite can't translate a DateTimeOffset range
        // combined with string-converted enum comparisons, so we filter dated tasks in memory
        // (the candidate set is small at personal scale).
        var endOfToday = new DateTimeOffset(DateTime.Today).AddDays(1);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var dated = await db.Tasks
            .AsNoTracking()
            .Include(t => t.Subtasks)
            .Where(t => t.DueAt != null)
            .ToListAsync(cancellationToken);

        var today = dated
            .Where(t => t.DueAt < endOfToday
                        && t.Status != TaskStatus.Completed
                        && t.Status != TaskStatus.Canceled)
            .OrderBy(t => t.DueAt)
            .ToList();

        await PopulateTimeSpentAsync(db, today, cancellationToken);
        return today;
    }

    public async Task<TaskItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await db.Tasks
            .AsNoTracking()
            .Include(t => t.Subtasks)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (task is not null)
        {
            await PopulateTimeSpentAsync(db, [task], cancellationToken);
        }

        return task;
    }

    public async Task<TaskItem> CreateAsync(TaskDraft draft, CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft);

        var now = DateTimeOffset.Now;
        var task = new TaskItem
        {
            ProjectId = draft.ProjectId,
            Title = draft.Title.Trim(),
            Description = Normalize(draft.Description),
            Status = draft.Status,
            Priority = draft.Priority,
            DueAt = draft.DueAt,
            RequiredTime = ToRequiredTime(draft.RequiredMinutes),
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = draft.Status == TaskStatus.Completed ? now : null
        };

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        db.Tasks.Add(task);
        await db.SaveChangesAsync(cancellationToken);
        return task;
    }

    public async Task UpdateAsync(Guid id, TaskDraft draft, CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Task could not be found.");

        var wasCompleted = task.Status == TaskStatus.Completed;

        task.ProjectId = draft.ProjectId;
        task.Title = draft.Title.Trim();
        task.Description = Normalize(draft.Description);
        task.Priority = draft.Priority;
        task.DueAt = draft.DueAt;
        task.RequiredTime = ToRequiredTime(draft.RequiredMinutes);

        if (draft.Status == TaskStatus.Completed)
        {
            await EnsureRequiredTimeMetAsync(db, task, cancellationToken);
        }

        ApplyStatus(task, draft.Status);
        task.UpdatedAt = DateTimeOffset.Now;

        RecordCompletionEvidence(db, task, wasCompleted);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetStatusAsync(Guid id, TaskStatus status, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Task could not be found.");

        var wasCompleted = task.Status == TaskStatus.Completed;

        if (status == TaskStatus.Completed)
        {
            await EnsureRequiredTimeMetAsync(db, task, cancellationToken);
        }

        ApplyStatus(task, status);
        task.UpdatedAt = DateTimeOffset.Now;

        RecordCompletionEvidence(db, task, wasCompleted);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (task is null)
        {
            return;
        }

        db.Tasks.Remove(task);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<SubtaskItem> AddSubtaskAsync(Guid taskId, string title, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException("Subtask title is required.");
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var taskExists = await db.Tasks.AnyAsync(t => t.Id == taskId, cancellationToken);
        if (!taskExists)
        {
            throw new InvalidOperationException("Task could not be found.");
        }

        var nextOrder = await db.Subtasks
            .Where(s => s.TaskItemId == taskId)
            .CountAsync(cancellationToken);

        var now = DateTimeOffset.Now;
        var subtask = new SubtaskItem
        {
            TaskItemId = taskId,
            Title = title.Trim(),
            SortOrder = nextOrder,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Subtasks.Add(subtask);
        await db.SaveChangesAsync(cancellationToken);
        return subtask;
    }

    public async Task SetSubtaskCompletedAsync(Guid subtaskId, bool isCompleted, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var subtask = await db.Subtasks
            .Include(s => s.TaskItem)
            .FirstOrDefaultAsync(s => s.Id == subtaskId, cancellationToken)
            ?? throw new InvalidOperationException("Subtask could not be found.");

        var wasCompleted = subtask.IsCompleted;
        subtask.IsCompleted = isCompleted;
        subtask.UpdatedAt = DateTimeOffset.Now;

        if (isCompleted && !wasCompleted)
        {
            db.Evidence.Add(new EvidenceItem
            {
                Type = EvidenceType.SubtaskCompleted,
                Source = "subtask",
                Title = subtask.Title,
                ProjectId = subtask.TaskItem?.ProjectId,
                TaskId = subtask.TaskItemId,
                OccurredAt = DateTimeOffset.Now
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteSubtaskAsync(Guid subtaskId, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var subtask = await db.Subtasks.FirstOrDefaultAsync(s => s.Id == subtaskId, cancellationToken);
        if (subtask is null)
        {
            return;
        }

        db.Subtasks.Remove(subtask);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static TimeSpan? ToRequiredTime(int? minutes) =>
        minutes is > 0 ? TimeSpan.FromMinutes(minutes.Value) : null;

    /// <summary>Throws if the task has a required focus time that its accumulated time hasn't met yet.</summary>
    private static async Task EnsureRequiredTimeMetAsync(LtfiDbContext db, TaskItem task, CancellationToken cancellationToken)
    {
        if (task.RequiredTime is not { } required)
        {
            return;
        }

        var spent = await SumCompletedSessionTimeAsync(db, task.Id, cancellationToken);
        if (spent < required)
        {
            throw new InvalidOperationException(
                $"This task needs {(int)required.TotalMinutes} min of focus before it can be completed " +
                $"— {(int)spent.TotalMinutes} min logged so far.");
        }
    }

    private static async Task<TimeSpan> SumCompletedSessionTimeAsync(LtfiDbContext db, Guid taskId, CancellationToken cancellationToken)
    {
        var sessions = await db.FocusSessions
            .AsNoTracking()
            .Where(s => s.TaskId == taskId)
            .Select(s => new { s.Status, s.Duration })
            .ToListAsync(cancellationToken);

        return sessions
            .Where(s => s.Status == FocusSessionStatus.Completed && s.Duration != null)
            .Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Duration!.Value);
    }

    /// <summary>Sums each task's completed focus-session durations into <see cref="TaskItem.TimeSpent"/>.</summary>
    private static async Task PopulateTimeSpentAsync(
        LtfiDbContext db,
        IReadOnlyCollection<TaskItem> tasks,
        CancellationToken cancellationToken)
    {
        if (tasks.Count == 0)
        {
            return;
        }

        // Status is filtered in memory to avoid SQLite's string-enum translation limits.
        var sessions = await db.FocusSessions
            .AsNoTracking()
            .Where(s => s.TaskId != null)
            .Select(s => new { s.TaskId, s.Status, s.Duration })
            .ToListAsync(cancellationToken);

        var totals = sessions
            .Where(s => s.Status == FocusSessionStatus.Completed && s.Duration != null)
            .GroupBy(s => s.TaskId!.Value)
            .ToDictionary(g => g.Key, g => g.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Duration!.Value));

        foreach (var task in tasks)
        {
            task.TimeSpent = totals.TryGetValue(task.Id, out var total) ? total : TimeSpan.Zero;
        }
    }

    private static void ApplyStatus(TaskItem task, TaskStatus status)
    {
        task.Status = status;
        task.CompletedAt = status == TaskStatus.Completed
            ? task.CompletedAt ?? DateTimeOffset.Now
            : null;
    }

    /// <summary>Writes a TaskCompleted evidence record when a task first transitions to Completed.</summary>
    private static void RecordCompletionEvidence(LtfiDbContext db, TaskItem task, bool wasCompleted)
    {
        if (task.Status == TaskStatus.Completed && !wasCompleted)
        {
            db.Evidence.Add(new EvidenceItem
            {
                Type = EvidenceType.TaskCompleted,
                Source = "task",
                Title = task.Title,
                ProjectId = task.ProjectId,
                TaskId = task.Id,
                OccurredAt = DateTimeOffset.Now
            });
        }
    }

    private static void ValidateDraft(TaskDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Title))
        {
            throw new InvalidOperationException("Task title is required.");
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
