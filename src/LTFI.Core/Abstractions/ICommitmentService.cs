using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LTFI.Core.Domain;

namespace LTFI.Core.Abstractions;

/// <summary>A weekly commitment enriched with its linked task, for display.</summary>
public sealed record CommitmentLine(
    Guid Id,
    Guid CheckInId,
    DateOnly WeekStart,
    string Text,
    CommitmentStatus Status,
    DateTimeOffset? ResolvedAt,
    int SortOrder,
    Guid? LinkedTaskId,
    string? LinkedTaskTitle,
    string? LinkedArea,
    DateTimeOffset? LinkedDue,
    bool LinkedTaskOpen)
{
    public bool IsLinked => LinkedTaskId is not null;

    public bool IsKept => Status == CommitmentStatus.Kept;
}

/// <summary>An open reminder-backed task a commitment can be linked to (the check-in picker).</summary>
public sealed record LinkableTask(Guid Id, string Title, string? Area, DateTimeOffset? DueAt);

/// <summary>
/// Weekly commitments (check-in Q5 made real). Commitments are created by
/// <see cref="IReflectionService.SaveWeeklyCheckInAsync(IReadOnlyList{string?}, IReadOnlyList{CommitmentDraft}, IReadOnlyDictionary{Guid, CommitmentStatus}?, CancellationToken)"/>;
/// this service reads and keeps them. Every read first <em>reconciles</em>: an open commitment
/// whose linked task is Completed becomes Kept (with its one <see cref="EvidenceType.CommitmentKept"/>
/// item), so it works no matter how the completion arrived (LTFI or the reminders sync).
/// </summary>
public interface ICommitmentService
{
    /// <summary>The current check-in week's commitments (Dropped ones excluded), in order.
    /// Also back-fills commitments for a v1 check-in made this week with only free-text Q5.</summary>
    Task<IReadOnlyList<CommitmentLine>> GetCurrentWeekAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Earlier weeks' commitments the next check-in reviews: the most recent earlier week that has
    /// commitments (Dropped excluded) plus any still-Open ones from older weeks. Empty once all are resolved.
    /// </summary>
    Task<IReadOnlyList<CommitmentLine>> GetPendingReviewAsync(CancellationToken cancellationToken = default);

    /// <summary>Marks an Open commitment Kept and writes its evidence once. No-op when already Kept.</summary>
    Task KeepAsync(Guid commitmentId, CancellationToken cancellationToken = default);

    /// <summary>Open reminder-backed tasks for the link picker (soonest due first).</summary>
    Task<IReadOnlyList<LinkableTask>> GetLinkableTasksAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates commitments for this week's latest check-in from its free-text Q5 when it has
    /// none (v1 check-ins). Idempotent. Returns how many were created.</summary>
    Task<int> BackfillCurrentWeekAsync(CancellationToken cancellationToken = default);
}
