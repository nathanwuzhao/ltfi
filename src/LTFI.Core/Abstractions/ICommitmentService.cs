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
    /// <summary>The commitments that apply to the current Mon–Sun week (made in the check-in that
    /// reviewed last week), Dropped excluded, in order. Also back-fills commitments for recent v1
    /// check-ins with only free-text Q5.</summary>
    Task<IReadOnlyList<CommitmentLine>> GetCurrentWeekAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// What the Command Center shows: this week's commitments, or — once this week's own check-in
    /// is in (the Sat/Sun window), which settled them — next week's, flagged <see cref="CommitmentPanel.IsNextWeek"/>.
    /// </summary>
    Task<CommitmentPanel> GetPanelAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The commitments the check-in being written reviews: those that applied to its review week
    /// (Dropped excluded) plus any still-Open ones from older weeks. Empty once the review week's
    /// check-in is in and nothing is Open.
    /// </summary>
    Task<IReadOnlyList<CommitmentLine>> GetPendingReviewAsync(CancellationToken cancellationToken = default);

    /// <summary>Marks an Open commitment Kept and writes its evidence once. No-op when already Kept.</summary>
    Task KeepAsync(Guid commitmentId, CancellationToken cancellationToken = default);

    /// <summary>Open reminder-backed tasks for the link picker (soonest due first).</summary>
    Task<IReadOnlyList<LinkableTask>> GetLinkableTasksAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates commitments from the free-text Q5 of the latest check-in for last week's and
    /// this week's review (whose commitments apply to this week and next) when it has none (v1
    /// check-ins). Older check-ins are never back-filled. Idempotent. Returns how many were created.</summary>
    Task<int> BackfillCurrentWeekAsync(CancellationToken cancellationToken = default);
}

/// <summary>The Command Center's commitments: the week (Monday) they apply to and whether that is next week.</summary>
public sealed record CommitmentPanel(DateOnly WeekStart, bool IsNextWeek, IReadOnlyList<CommitmentLine> Lines);
