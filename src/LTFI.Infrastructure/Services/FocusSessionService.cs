using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using LTFI.Infrastructure.Persistence;
using TaskStatus = LTFI.Core.Domain.TaskStatus;

namespace LTFI.Infrastructure.Services;

/// <summary>
/// Owns the single active focus session. Registered as a singleton so the in-memory elapsed
/// timer survives view navigation; the session row is persisted on pause and finish.
/// </summary>
public sealed class FocusSessionService(IDbContextFactory<LtfiDbContext> contextFactory) : IFocusSessionService
{
    private readonly IDbContextFactory<LtfiDbContext> _contextFactory = contextFactory;
    private readonly object _gate = new();

    // In-memory state for the active session.
    private Guid? _activeId;
    private Guid? _projectId;
    private Guid? _taskId;
    private string? _taskTitle;
    private string? _intent;
    private FocusSessionStatus _status;
    private TimeSpan _accumulated;        // active time banked before the current running segment
    private DateTimeOffset? _runningSince; // start of the current running segment; null when paused

    public bool HasActiveSession
    {
        get { lock (_gate) { return _activeId is not null; } }
    }

    public ActiveFocusSnapshot? GetActiveSnapshot()
    {
        lock (_gate)
        {
            if (_activeId is null)
            {
                return null;
            }

            return new ActiveFocusSnapshot(
                _activeId.Value, _projectId, _taskId, _taskTitle, _intent, _status, ElapsedNoLock());
        }
    }

    public async Task<FocusSession> StartAsync(Guid? projectId, Guid? taskId, string? intent, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_activeId is not null)
            {
                throw new InvalidOperationException("A focus session is already in progress.");
            }
        }

        var now = DateTimeOffset.Now;
        var session = new FocusSession
        {
            ProjectId = projectId,
            TaskId = taskId,
            Intent = string.IsNullOrWhiteSpace(intent) ? null : intent.Trim(),
            StartedAt = now,
            Status = FocusSessionStatus.Active,
            Duration = TimeSpan.Zero
        };

        string? taskTitle = null;

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        db.FocusSessions.Add(session);

        if (taskId is { } id)
        {
            var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
            if (task is not null)
            {
                taskTitle = task.Title;
                if (task.Status == TaskStatus.Ready)
                {
                    task.Status = TaskStatus.InProgress;
                    task.UpdatedAt = now;
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        lock (_gate)
        {
            _activeId = session.Id;
            _projectId = projectId;
            _taskId = taskId;
            _taskTitle = taskTitle;
            _intent = session.Intent;
            _status = FocusSessionStatus.Active;
            _accumulated = TimeSpan.Zero;
            _runningSince = now;
        }

        return session;
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        Guid id;
        TimeSpan banked;
        lock (_gate)
        {
            if (_activeId is null || _status != FocusSessionStatus.Active)
            {
                return;
            }

            _accumulated = ElapsedNoLock();
            _runningSince = null;
            _status = FocusSessionStatus.Paused;
            id = _activeId.Value;
            banked = _accumulated;
        }

        await PersistAsync(id, s =>
        {
            s.Duration = banked;
            s.Status = FocusSessionStatus.Paused;
        }, cancellationToken);
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        Guid id;
        lock (_gate)
        {
            if (_activeId is null || _status != FocusSessionStatus.Paused)
            {
                return;
            }

            _runningSince = DateTimeOffset.Now;
            _status = FocusSessionStatus.Active;
            id = _activeId.Value;
        }

        await PersistAsync(id, s => s.Status = FocusSessionStatus.Active, cancellationToken);
    }

    public async Task<FocusSession> FinishAsync(
        FocusSessionResult result,
        string? resultSummary,
        string? blockerSummary,
        string? nextAction,
        CancellationToken cancellationToken = default)
    {
        Guid id;
        TimeSpan elapsed;
        Guid? projectId;
        Guid? taskId;
        string? intent;
        lock (_gate)
        {
            if (_activeId is null)
            {
                throw new InvalidOperationException("There is no active focus session to finish.");
            }

            elapsed = ElapsedNoLock();
            id = _activeId.Value;
            projectId = _projectId;
            taskId = _taskId;
            intent = _intent;
        }

        var now = DateTimeOffset.Now;

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.FocusSessions.FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Focus session could not be found.");

        session.Duration = elapsed;
        session.EndedAt = now;
        session.Status = FocusSessionStatus.Completed;
        session.Result = result;
        session.ResultSummary = Normalize(resultSummary);
        session.BlockerSummary = Normalize(blockerSummary);
        session.NextAction = Normalize(nextAction);

        db.Evidence.Add(new EvidenceItem
        {
            Type = EvidenceType.FocusSessionCompleted,
            Source = "focus",
            Title = string.IsNullOrWhiteSpace(intent) ? "Focus session" : intent!,
            ProjectId = projectId,
            TaskId = taskId,
            FocusSessionId = id,
            OccurredAt = now
        });

        await db.SaveChangesAsync(cancellationToken);

        ClearActive();
        return session;
    }

    public async Task AbandonAsync(CancellationToken cancellationToken = default)
    {
        Guid id;
        TimeSpan elapsed;
        lock (_gate)
        {
            if (_activeId is null)
            {
                return;
            }

            elapsed = ElapsedNoLock();
            id = _activeId.Value;
        }

        await PersistAsync(id, s =>
        {
            s.Duration = elapsed;
            s.EndedAt = DateTimeOffset.Now;
            s.Status = FocusSessionStatus.Abandoned;
        }, cancellationToken);

        ClearActive();
    }

    public async Task AbandonDanglingSessionsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var dangling = await db.FocusSessions
            .Where(s => s.Status == FocusSessionStatus.Active || s.Status == FocusSessionStatus.Paused)
            .ToListAsync(cancellationToken);

        if (dangling.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        foreach (var session in dangling)
        {
            session.Status = FocusSessionStatus.Abandoned;
            session.EndedAt ??= now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private TimeSpan ElapsedNoLock() =>
        _accumulated + (_runningSince is { } since ? DateTimeOffset.Now - since : TimeSpan.Zero);

    private void ClearActive()
    {
        lock (_gate)
        {
            _activeId = null;
            _projectId = null;
            _taskId = null;
            _taskTitle = null;
            _intent = null;
            _accumulated = TimeSpan.Zero;
            _runningSince = null;
        }
    }

    private async Task PersistAsync(Guid id, Action<FocusSession> mutate, CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.FocusSessions.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (session is null)
        {
            return;
        }

        mutate(session);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
