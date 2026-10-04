using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LTFI.Core.Domain;

namespace LTFI.Core.Abstractions;

/// <summary>A saved weekly check-in with its answers parsed back out of the stored JSON body.</summary>
public sealed record WeeklyCheckInRecord(
    Guid Id,
    DateTimeOffset CreatedAt,
    IReadOnlyList<WeeklyCheckInAnswer> Answers);

/// <summary>Where the weekly check-in gate stands right now.</summary>
public sealed record WeeklyCheckInStatus(
    bool IsDue,
    bool IsSnoozed,
    DateTimeOffset? SnoozedUntil,
    int SnoozesRemaining,
    DateTimeOffset? LastCheckInAt,
    DateTimeOffset NextDueAt)
{
    /// <summary>True when the shell should force the check-in page and lock navigation.</summary>
    public bool MustShow => IsDue && !IsSnoozed;

    public bool CanSnooze => IsDue && SnoozesRemaining > 0;
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
    /// Saves a weekly check-in (answers in <see cref="WeeklyCheckIn.Questions"/> order). The first
    /// check-in of a week also records a <see cref="EvidenceType.ReflectionSubmitted"/> evidence item.
    /// </summary>
    Task<WeeklyCheckInRecord> SaveWeeklyCheckInAsync(
        IReadOnlyList<string?> answers, CancellationToken cancellationToken = default);

    Task<WeeklyCheckInRecord?> GetLatestWeeklyCheckInAsync(CancellationToken cancellationToken = default);

    /// <summary>Most recent check-ins first, capped at <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<WeeklyCheckInRecord>> GetWeeklyCheckInHistoryAsync(
        int limit = 10, CancellationToken cancellationToken = default);

    Task<WeeklyCheckInStatus> GetWeeklyCheckInStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Snoozes a due check-in; throws when none is due or the week's snoozes are used up.</summary>
    Task<WeeklyCheckInStatus> SnoozeWeeklyCheckInAsync(CancellationToken cancellationToken = default);
}
