using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using LTFI.Infrastructure.Persistence;
using LTFI.Infrastructure.Reminders;
using LTFI.Infrastructure.Settings;
using TaskStatus = LTFI.Core.Domain.TaskStatus;

namespace LTFI.Infrastructure.Services;

/// <summary>
/// Persistence-backed <see cref="ITaskService"/> including subtask management.
/// iCloud Reminders is the only source of tasks: <see cref="CreateAsync"/> never makes a local-only
/// task. It makes a reminder-backed task pending on the iPhone (ExternalId = a fresh
/// <c>ltfi://r/…</c> url) plus an outbox "create" command, and completing a reminder-backed task
/// queues a "complete" command. Subtasks stay LTFI-local checklists.
/// </summary>
public sealed class TaskService(
    IDbContextFactory<LtfiDbContext> contextFactory,
    IReminderOutbox? outbox = null,
    RemindersSettings? settings = null) : ITaskService
{
    /// <summary>Shown when a reminder can't be targeted by the outbox (no ltfi:// URL yet).</summary>
    public const string NoLtfiIdMessage =
        "This reminder can't be completed from LTFI yet: complete it on your phone " +
        "(no LTFI id yet — export with URL stamping first).";

    /// <summary>Shown when a due-date change can't be pushed (no ltfi:// URL yet).</summary>
    public const string NoLtfiIdDueMessage =
        "This reminder's due date can't be changed from LTFI yet: change it on your phone " +
        "(no LTFI id yet — export with URL stamping first).";

    /// <summary>The outbox can set a due date but not remove one.</summary>
    public const string CannotClearDueMessage =
        "LTFI can't remove a reminder's due date on the iPhone — clear it there (or pick another date).";

    private readonly IDbContextFactory<LtfiDbContext> _contextFactory = contextFactory;
    private readonly IReminderOutbox? _outbox = outbox;
    private readonly RemindersSettings _settings = settings ?? new RemindersSettings();

    public async Task<IReadOnlyList<TaskItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        // SQLite can't ORDER BY a DateTimeOffset column, so order newest-first in memory.
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var tasks = await db.Tasks
            .AsNoTracking()
            .Include(t => t.Subtasks)
            .Include(t => t.Area)
            .Include(t => t.Project) // read-only: the UI's project code/colour tag
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
            .Include(t => t.Area)
            .Include(t => t.Project) // read-only: the UI's project code/colour tag
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
            .Include(t => t.Area)
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

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var (project, area) = await ResolvePlacementAsync(db, draft, requireStandingArea: true, cancellationToken);

        // Standing-project tasks go into the list named by their area; everything else into the
        // LTFI list. The project/area recorded here is kept by the sync (LTFI-minted url).
        var list = TargetList(project, area);

        var now = DateTimeOffset.Now;
        var task = new TaskItem
        {
            ProjectId = project?.Id,
            AreaId = area?.Id,
            Title = draft.Title.Trim(),
            Description = Normalize(draft.Description),
            Status = draft.Status,
            Priority = draft.Priority,
            DueAt = DueDates.EndOfDay(draft.DueAt), // due is a date: stored and sent as 23:59
            RequiredTime = ToRequiredTime(draft.RequiredMinutes),
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = draft.Status == TaskStatus.Completed ? now : null,
            ExternalSource = ReminderRules.SourceKey,
            ExternalId = ReminderRules.NewLtfiUrl(),
            ExternalList = list
        };

        db.Tasks.Add(task);
        ReminderOutbox.EnqueueCreate(db, task, now);
        if (task.Status == TaskStatus.Completed)
        {
            ReminderOutbox.EnqueueComplete(db, task, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await FlushOutboxAsync(cancellationToken);
        return task;
    }

    public async Task UpdateAsync(Guid id, TaskDraft draft, CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Task could not be found.");

        // A phone-made reminder's placement comes from its list (the sync re-derives it), so only
        // tasks LTFI placed itself must name an area under a standing project.
        var placedByLtfi = task.ExternalSource is null || ReminderRules.IsLtfiCreatedUrl(task.ExternalId);
        var (project, area) = await ResolvePlacementAsync(db, draft, placedByLtfi, cancellationToken);
        var wasCompleted = task.Status == TaskStatus.Completed;

        // A new due date on a reminder the iPhone already has goes out as an "update" command (while
        // its create is still pending, the create below carries it instead).
        // Due is a date: a changed date is stored (and sent) as that local date at 23:59; an
        // unchanged one keeps the task's existing value, so a phone-set time isn't rewritten.
        var dueChanged = task.DueAt != draft.DueAt;
        var newDue = dueChanged ? DueDates.EndOfDay(draft.DueAt) : task.DueAt;
        var pushDue = dueChanged
                      && task.ExternalSource == ReminderRules.SourceKey
                      && await FindPendingCreateAsync(db, task, cancellationToken) is null;
        if (pushDue)
        {
            EnsureCanPushDue(task, newDue);
        }

        task.ProjectId = project?.Id;
        task.AreaId = area?.Id;
        task.Title = draft.Title.Trim();
        task.Description = Normalize(draft.Description);
        task.Priority = draft.Priority;
        task.DueAt = newDue;
        task.RequiredTime = ToRequiredTime(draft.RequiredMinutes);

        if (draft.Status == TaskStatus.Completed && !wasCompleted)
        {
            await EnsureCanCompleteAsync(db, task, cancellationToken);
        }

        ApplyStatus(task, draft.Status);
        var now = DateTimeOffset.Now;
        task.UpdatedAt = now;

        // Still waiting for the iPhone to create it: the create command carries the edits.
        var outboxChanged = await RefreshPendingCreateAsync(db, task, project, area, cancellationToken);
        if (pushDue)
        {
            await QueueDueUpdateAsync(db, task, now, cancellationToken);
            outboxChanged = true;
        }
        outboxChanged |= QueueCompletionWriteBack(db, task, wasCompleted, now);

        RecordCompletionEvidence(db, task, wasCompleted);
        await db.SaveChangesAsync(cancellationToken);
        if (outboxChanged)
        {
            await FlushOutboxAsync(cancellationToken);
        }
    }

    public async Task SetStatusAsync(Guid id, TaskStatus status, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Task could not be found.");

        var wasCompleted = task.Status == TaskStatus.Completed;

        if (status == TaskStatus.Completed && !wasCompleted)
        {
            await EnsureCanCompleteAsync(db, task, cancellationToken);
        }

        ApplyStatus(task, status);
        var now = DateTimeOffset.Now;
        task.UpdatedAt = now;

        var outboxChanged = QueueCompletionWriteBack(db, task, wasCompleted, now);
        RecordCompletionEvidence(db, task, wasCompleted);
        await db.SaveChangesAsync(cancellationToken);
        if (outboxChanged)
        {
            await FlushOutboxAsync(cancellationToken);
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (task is null)
        {
            return;
        }

        // Commands the iPhone hasn't applied yet are withdrawn, so a deleted draft never reaches it.
        var unsent = task.ExternalId is { } url
            ? await db.Outbox.Where(c => c.ExternalUrl == url && c.ConfirmedAt == null).ToListAsync(cancellationToken)
            : [];
        db.Outbox.RemoveRange(unsent);

        db.Tasks.Remove(task);
        await db.SaveChangesAsync(cancellationToken);
        if (unsent.Count > 0)
        {
            await FlushOutboxAsync(cancellationToken);
        }
    }

    public async Task SetDueDateAsync(Guid id, DateTimeOffset due, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Task could not be found.");

        await SetDueCoreAsync(db, task, due, cancellationToken);
    }

    public async Task<DateTimeOffset> PushDueByDaysAsync(Guid id, int days, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Task could not be found.");

        var due = ShiftDue(task.DueAt, days, DateTime.Today);
        await SetDueCoreAsync(db, task, due, cancellationToken);
        return due;
    }

    /// <summary>
    /// (<paramref name="due"/>'s local date + <paramref name="days"/>) at 23:59, whatever time it had;
    /// with no due date, counts from <paramref name="today"/>. See <see cref="DueDates.Shift"/>.
    /// </summary>
    public static DateTimeOffset ShiftDue(DateTimeOffset? due, int days, DateTime today) =>
        DueDates.Shift(due, days, today);

    private async Task SetDueCoreAsync(LtfiDbContext db, TaskItem task, DateTimeOffset due, CancellationToken cancellationToken)
    {
        due = DueDates.EndOfDay(due); // due is a date: stored and sent as 23:59
        var now = DateTimeOffset.Now;
        var outboxChanged = false;
        if (task.ExternalSource == ReminderRules.SourceKey)
        {
            EnsureCanPushDue(task, due);
            task.DueAt = due;
            if (await FindPendingCreateAsync(db, task, cancellationToken) is { } create)
            {
                // The iPhone hasn't made it yet: the create carries the new date.
                create.PayloadJson = ReminderOutbox.CreatePayload(task);
            }
            else
            {
                await QueueDueUpdateAsync(db, task, now, cancellationToken);
            }

            outboxChanged = true;
        }
        else
        {
            task.DueAt = due;
        }

        task.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        if (outboxChanged)
        {
            await FlushOutboxAsync(cancellationToken);
        }
    }

    /// <summary>The outbox can only target a reminder by its ltfi:// url, and can't remove a due date.</summary>
    private static void EnsureCanPushDue(TaskItem task, DateTimeOffset? due)
    {
        if (!ReminderRules.IsLtfiUrl(task.ExternalId))
        {
            throw new InvalidOperationException(NoLtfiIdDueMessage);
        }

        if (due is null)
        {
            throw new InvalidOperationException(CannotClearDueMessage);
        }
    }

    /// <summary>Queues (or coalesces into the pending) "update" command with the task's DueAt.</summary>
    private static async Task QueueDueUpdateAsync(LtfiDbContext db, TaskItem task, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var url = task.ExternalId!;
        var pending = await db.Outbox.FirstOrDefaultAsync(
            c => c.ExternalUrl == url && c.Op == OutboxCommand.UpdateOp && c.ConfirmedAt == null, cancellationToken);
        ReminderOutbox.EnqueueUpdate(db, task, pending, now);
    }

    private static async Task<OutboxCommand?> FindPendingCreateAsync(LtfiDbContext db, TaskItem task, CancellationToken cancellationToken)
    {
        if (task.ExternalId is not { } url || !ReminderRules.IsLtfiCreatedUrl(url))
        {
            return null;
        }

        return await db.Outbox.FirstOrDefaultAsync(
            c => c.ExternalUrl == url && c.Op == OutboxCommand.CreateOp && c.ConfirmedAt == null, cancellationToken);
    }

    /// <summary>The list a task placed in this project/area goes into (<see cref="ReminderRules.TargetList"/>).
    /// Callers have already checked that a standing project has an area.</summary>
    private string TargetList(Project? project, ProjectArea? area) =>
        ReminderRules.TargetList(project?.IsStanding == true, area?.Name, _settings.LtfiList)
        ?? throw new InvalidOperationException("Pick an area — it's the iPhone list this goes to.");

    /// <summary>Loads and checks the draft's project/area: the area must belong to the project, and a
    /// standing project needs an area (its name is the Reminders list).</summary>
    private static async Task<(Project? Project, ProjectArea? Area)> ResolvePlacementAsync(
        LtfiDbContext db, TaskDraft draft, bool requireStandingArea, CancellationToken cancellationToken)
    {
        Project? project = null;
        if (draft.ProjectId is { } projectId)
        {
            project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
                ?? throw new InvalidOperationException("Project could not be found.");
        }

        ProjectArea? area = null;
        if (draft.AreaId is { } areaId)
        {
            area = await db.Areas.AsNoTracking().FirstOrDefaultAsync(a => a.Id == areaId, cancellationToken)
                ?? throw new InvalidOperationException("Area could not be found.");
            if (area.ProjectId != project?.Id)
            {
                throw new InvalidOperationException("That area belongs to a different project.");
            }
        }

        if (requireStandingArea && project is { IsStanding: true } && area is null)
        {
            throw new InvalidOperationException(
                $"Pick an area for {project.Title} tasks — the area is the iPhone Reminders list it goes into.");
        }

        return (project, area);
    }

    /// <summary>
    /// The focus gate, plus the write-back rule: a reminder-backed task can only be completed from
    /// LTFI when it has an <c>ltfi://</c> url the "LTFI Apply" Shortcut can find it by.
    /// </summary>
    private static async Task EnsureCanCompleteAsync(LtfiDbContext db, TaskItem task, CancellationToken cancellationToken)
    {
        if (task.ExternalSource == ReminderRules.SourceKey && !ReminderRules.IsLtfiUrl(task.ExternalId))
        {
            throw new InvalidOperationException(NoLtfiIdMessage);
        }

        await EnsureRequiredTimeMetAsync(db, task, cancellationToken);
    }

    /// <summary>Queues a "complete" command when a reminder-backed task becomes Completed in LTFI.</summary>
    private static bool QueueCompletionWriteBack(LtfiDbContext db, TaskItem task, bool wasCompleted, DateTimeOffset now)
    {
        if (wasCompleted || task.Status != TaskStatus.Completed
            || task.ExternalSource != ReminderRules.SourceKey || !ReminderRules.IsLtfiUrl(task.ExternalId))
        {
            return false;
        }

        ReminderOutbox.EnqueueComplete(db, task, now);
        return true;
    }

    /// <summary>Rewrites an unconfirmed create command's payload after the task was edited.</summary>
    private async Task<bool> RefreshPendingCreateAsync(
        LtfiDbContext db, TaskItem task, Project? project, ProjectArea? area, CancellationToken cancellationToken)
    {
        var create = await FindPendingCreateAsync(db, task, cancellationToken);
        if (create is null)
        {
            return false;
        }

        task.ExternalList = TargetList(project, area);
        create.PayloadJson = ReminderOutbox.CreatePayload(task);
        return true;
    }

    private async Task FlushOutboxAsync(CancellationToken cancellationToken)
    {
        if (_outbox is not null)
        {
            await _outbox.FlushAsync(cancellationToken);
        }
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
