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

        return dated
            .Where(t => t.DueAt < endOfToday
                        && t.Status != TaskStatus.Completed
                        && t.Status != TaskStatus.Canceled)
            .OrderBy(t => t.DueAt)
            .ToList();
    }

    public async Task<TaskItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Tasks
            .AsNoTracking()
            .Include(t => t.Subtasks)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
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

        task.ProjectId = draft.ProjectId;
        task.Title = draft.Title.Trim();
        task.Description = Normalize(draft.Description);
        task.Priority = draft.Priority;
        task.DueAt = draft.DueAt;
        ApplyStatus(task, draft.Status);
        task.UpdatedAt = DateTimeOffset.Now;

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetStatusAsync(Guid id, TaskStatus status, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Task could not be found.");

        ApplyStatus(task, status);
        task.UpdatedAt = DateTimeOffset.Now;
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
        var subtask = await db.Subtasks.FirstOrDefaultAsync(s => s.Id == subtaskId, cancellationToken)
            ?? throw new InvalidOperationException("Subtask could not be found.");

        subtask.IsCompleted = isCompleted;
        subtask.UpdatedAt = DateTimeOffset.Now;
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

    private static void ApplyStatus(TaskItem task, TaskStatus status)
    {
        task.Status = status;
        task.CompletedAt = status == TaskStatus.Completed
            ? task.CompletedAt ?? DateTimeOffset.Now
            : null;
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
