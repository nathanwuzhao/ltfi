using System;
using System.Threading;
using System.Threading.Tasks;
using LTFI.Core.Domain;

namespace LTFI.Core.Abstractions;

/// <summary>A live view of the in-progress focus session (held in memory while the app runs).</summary>
public sealed record ActiveFocusSnapshot(
    Guid Id,
    Guid? ProjectId,
    Guid? TaskId,
    string? TaskTitle,
    string? Intent,
    FocusSessionStatus Status,
    TimeSpan Elapsed);

/// <summary>
/// Drives the single active focus session: start, pause/resume, finish, abandon. The authoritative
/// elapsed time is kept in memory by the implementation (a singleton) so it survives navigation;
/// the session record is persisted on pause and finish.
/// </summary>
public interface IFocusSessionService
{
    bool HasActiveSession { get; }

    /// <summary>Snapshot of the active session with current elapsed time, or null if none.</summary>
    ActiveFocusSnapshot? GetActiveSnapshot();

    Task<FocusSession> StartAsync(Guid? projectId, Guid? taskId, string? intent, CancellationToken cancellationToken = default);

    Task PauseAsync(CancellationToken cancellationToken = default);

    Task ResumeAsync(CancellationToken cancellationToken = default);

    Task<FocusSession> FinishAsync(
        FocusSessionResult result,
        string? resultSummary,
        string? blockerSummary,
        string? nextAction,
        CancellationToken cancellationToken = default);

    Task AbandonAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Startup recovery: any session left Active/Paused by a previous run can't have its elapsed
    /// time trusted, so it is marked Abandoned. (Live cross-restart resume is a Phase 7 concern.)
    /// </summary>
    Task AbandonDanglingSessionsAsync(CancellationToken cancellationToken = default);
}
