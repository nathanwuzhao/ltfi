using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LTFI.Core.Domain;

namespace LTFI.Core.Abstractions;

/// <summary>
/// A saved weekly check-in with its answers parsed back out of the stored JSON body.
/// <see cref="ReviewWeek"/> (Monday) is derived from <see cref="CreatedAt"/> by
/// <see cref="WeeklyCheckIn.ReviewedWeekOf"/>; nothing extra is stored. <see cref="IsLate"/>: saved
/// after that week's deadline.
/// </summary>
public sealed record WeeklyCheckInRecord(
    Guid Id,
    DateTimeOffset CreatedAt,
    IReadOnlyList<WeeklyCheckInAnswer> Answers,
    DateOnly ReviewWeek,
    bool IsLate);

/// <summary>
/// Where the weekly check-in stands right now (see <see cref="WeeklyCheckIn.Evaluate"/>):
/// <see cref="ReviewWeek"/> is the Monday of the week the form reviews — or, when
/// <see cref="Phase"/> is Done, the week that was just reviewed.
/// </summary>
public sealed record WeeklyCheckInStatus(
    CheckInPhase Phase,
    DateOnly ReviewWeek,
    bool IsSnoozed,
    DateTimeOffset? SnoozedUntil,
    int SnoozesRemaining,
    int SnoozeHours,
    DateTimeOffset? LastCheckInAt,
    DateTimeOffset OpensAt,
    DateTimeOffset GateAt,
    DateTimeOffset DueAt,
    DateTimeOffset NextOpensAt,
    bool CanSubmit)
{
    /// <summary>A check-in is wanted (form + header chip): the window is open or it is overdue.</summary>
    public bool IsDue => Phase != CheckInPhase.Done;

    public bool IsOverdue => Phase == CheckInPhase.Overdue;

    /// <summary>From the gate time (or while overdue) until submitted.</summary>
    public bool IsGatePhase => Phase is CheckInPhase.Closing or CheckInPhase.Overdue;

    /// <summary>True when the shell should force the check-in page and lock navigation.</summary>
    public bool MustShow => IsGatePhase && !IsSnoozed;

    public bool CanSnooze => IsGatePhase && SnoozesRemaining > 0;

    /// <summary>Done, but still inside the window: submitting again revises it (no extra points).</summary>
    public bool IsRevision => Phase == CheckInPhase.Done && CanSubmit;
}

/// <summary>Persists the check-in snooze state outside the database (no migration needed).</summary>
public interface ICheckInSnoozeStore
{
    CheckInSnoozeState? Load();

    void Save(CheckInSnoozeState state);
}

/// <summary>
/// Reflections (plan §5 ReflectionEntry). v0 covers the mandatory weekly check-in: a fixed question
/// list stored as a <see cref="ReflectionScope.Week"/> entry, plus the due/snooze gate.
/// </summary>
public interface IReflectionService
{
    /// <summary>
    /// Saves a weekly check-in (answers in <see cref="WeeklyCheckIn.Questions"/> order). It reviews
    /// <see cref="WeeklyCheckIn.ReviewedWeekOf"/> its save time; the first check-in for a reviewed
    /// week also records a <see cref="EvidenceType.ReflectionSubmitted"/> evidence item.
    /// </summary>
    /// <remarks>Free-text form: the commitments are split out of the Q5 answer (as v1 check-ins were).</remarks>
    Task<WeeklyCheckInRecord> SaveWeeklyCheckInAsync(
        IReadOnlyList<string?> answers, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a weekly check-in with structured commitments (1–3 non-empty, each optionally linked to
    /// an open task). Q5 in <paramref name="answers"/> is replaced by the commitments joined as text,
    /// for history. The new commitments apply to the week after the reviewed one.
    /// <paramref name="resolutions"/> settles the Open commitments that applied to the reviewed week
    /// or earlier (Kept / Missed); any of those not listed becomes Missed. Open commitments from an
    /// earlier check-in for the same reviewed week are Dropped (superseded).
    /// </summary>
    Task<WeeklyCheckInRecord> SaveWeeklyCheckInAsync(
        IReadOnlyList<string?> answers,
        IReadOnlyList<CommitmentDraft> commitments,
        IReadOnlyDictionary<Guid, CommitmentStatus>? resolutions = null,
        CancellationToken cancellationToken = default);

    Task<WeeklyCheckInRecord?> GetLatestWeeklyCheckInAsync(CancellationToken cancellationToken = default);

    /// <summary>Most recent check-ins first, capped at <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<WeeklyCheckInRecord>> GetWeeklyCheckInHistoryAsync(
        int limit = 10, CancellationToken cancellationToken = default);

    Task<WeeklyCheckInStatus> GetWeeklyCheckInStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Snoozes the gate; throws unless it is up (closing/overdue) or when the reviewed week's snoozes are used up.</summary>
    Task<WeeklyCheckInStatus> SnoozeWeeklyCheckInAsync(CancellationToken cancellationToken = default);
}
