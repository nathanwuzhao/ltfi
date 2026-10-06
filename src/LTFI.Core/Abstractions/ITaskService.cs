using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LTFI.Core.Domain;
using TaskStatus = LTFI.Core.Domain.TaskStatus;

namespace LTFI.Core.Abstractions;

/// <summary>CRUD operations for tasks and their subtasks. Implemented over persistence in Infrastructure.</summary>
public interface ITaskService
{
    /// <summary>All tasks, ordered for display, with subtasks loaded.</summary>
    Task<IReadOnlyList<TaskItem>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Tasks due today or overdue and not yet completed/canceled.</summary>
    Task<IReadOnlyList<TaskItem>> GetTodayAsync(CancellationToken cancellationToken = default);

    Task<TaskItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<TaskItem> CreateAsync(TaskDraft draft, CancellationToken cancellationToken = default);

    Task UpdateAsync(Guid id, TaskDraft draft, CancellationToken cancellationToken = default);

    Task SetStatusAsync(Guid id, TaskStatus status, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a task's due date. For a reminder-backed task this is pushed to the iPhone through the
    /// outbox (an <c>update</c> command, or the pending <c>create</c>'s payload); the sync keeps the
    /// pushed date until an export confirms it. Throws for a reminder with no <c>ltfi://</c> url.
    /// </summary>
    Task SetDueDateAsync(Guid id, DateTimeOffset due, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the due date by <paramref name="days"/> keeping its time of day (local midnight stays
    /// midnight); with no due date, counts from today's local midnight. Returns the new due date.
    /// </summary>
    Task<DateTimeOffset> PushDueByDaysAsync(Guid id, int days, CancellationToken cancellationToken = default);

    // --- Subtasks ---

    Task<SubtaskItem> AddSubtaskAsync(Guid taskId, string title, CancellationToken cancellationToken = default);

    Task SetSubtaskCompletedAsync(Guid subtaskId, bool isCompleted, CancellationToken cancellationToken = default);

    Task DeleteSubtaskAsync(Guid subtaskId, CancellationToken cancellationToken = default);
}
