using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using LTFI.Infrastructure.Persistence;
using TaskStatus = LTFI.Core.Domain.TaskStatus;

namespace LTFI.Infrastructure.Services;

/// <summary>
/// Persistence-backed <see cref="ICommitmentService"/>. Linked commitments are kept by
/// <em>reconcile on read</em>: before returning anything, Open commitments whose linked task is
/// Completed are marked Kept (resolved at the task's completion time) and get their evidence. That
/// covers completions from TaskService and from the reminders sync alike, with no coupling between
/// those services and commitments. The clock is injectable for tests.
/// </summary>
public sealed class CommitmentService(
    IDbContextFactory<LtfiDbContext> contextFactory,
    TimeProvider? clock = null) : ICommitmentService
{
    private readonly IDbContextFactory<LtfiDbContext> _contextFactory = contextFactory;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private DateTimeOffset Now => _clock.GetLocalNow();

    public async Task<IReadOnlyList<CommitmentLine>> GetCurrentWeekAsync(CancellationToken cancellationToken = default)
    {
        await BackfillCurrentWeekAsync(cancellationToken);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await ReconcileAsync(db, cancellationToken);

        var week = WeeklyCommitments.WeekOf(Now);
        var rows = await db.Commitments.AsNoTracking()
            .Where(c => c.WeekStart == week && c.Status != CommitmentStatus.Dropped)
            .ToListAsync(cancellationToken);

        return await ToLinesAsync(db, rows, cancellationToken);
    }

    public async Task<IReadOnlyList<CommitmentLine>> GetPendingReviewAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await ReconcileAsync(db, cancellationToken);

        var week = WeeklyCommitments.WeekOf(Now);
        var earlier = await db.Commitments.AsNoTracking()
            .Where(c => c.WeekStart < week && c.Status != CommitmentStatus.Dropped)
            .ToListAsync(cancellationToken);
        if (earlier.Count == 0)
        {
            return [];
        }

        var lastWeek = earlier.Max(c => c.WeekStart);
        var rows = earlier
            .Where(c => c.WeekStart == lastWeek || c.Status == CommitmentStatus.Open)
            .ToList();

        // Once this week's check-in is in and nothing is left open, there is nothing to review.
        var checkedInThisWeek = await ReflectionService.HasCheckInSinceAsync(db, WeeklyCheckIn.WeekStart(Now), cancellationToken);
        if (checkedInThisWeek && rows.All(c => c.Status != CommitmentStatus.Open))
        {
            return [];
        }

        return await ToLinesAsync(db, rows, cancellationToken);
    }

    public async Task KeepAsync(Guid commitmentId, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var commitment = await db.Commitments.FirstOrDefaultAsync(c => c.Id == commitmentId, cancellationToken)
            ?? throw new InvalidOperationException("That commitment no longer exists.");

        if (commitment.Status == CommitmentStatus.Kept)
        {
            return;
        }

        if (commitment.Status != CommitmentStatus.Open)
        {
            throw new InvalidOperationException($"Only an open commitment can be kept (this one is {commitment.Status}).");
        }

        var task = commitment.LinkedTaskId is { } tid
            ? await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tid, cancellationToken)
            : null;
        await MarkKeptAsync(db, commitment, Now, task?.ProjectId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LinkableTask>> GetLinkableTasksAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var tasks = await db.Tasks.AsNoTracking()
            .Include(t => t.Area)
            .Where(t => t.ExternalSource != null
                        && t.ExternalRemovedAt == null
                        && t.Status != TaskStatus.Completed
                        && t.Status != TaskStatus.Canceled)
            .ToListAsync(cancellationToken);

        return tasks
            .OrderBy(t => t.DueAt is null)
            .ThenBy(t => t.DueAt)
            .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            .Select(t => new LinkableTask(t.Id, t.Title, t.Area?.Name, t.DueAt))
            .ToList();
    }

    public async Task<int> BackfillCurrentWeekAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var weekStart = WeeklyCheckIn.WeekStart(Now);
        var entries = await db.Reflections.AsNoTracking()
            .Where(r => r.ScopeType == ReflectionScope.Week && r.Prompt == WeeklyCheckIn.PromptVersion)
            .ToListAsync(cancellationToken);
        var latest = entries
            .Where(r => r.CreatedAt >= weekStart)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefault();
        if (latest is null || await db.Commitments.AnyAsync(c => c.CheckInId == latest.Id, cancellationToken))
        {
            return 0;
        }

        var answers = WeeklyCheckIn.Deserialize(latest.Body);
        var q5 = answers.Count > WeeklyCheckIn.CommitmentsQuestionIndex
            ? answers[WeeklyCheckIn.CommitmentsQuestionIndex].Answer
            : null;
        var texts = WeeklyCommitments.SplitLegacyAnswer(q5);
        AddCommitments(db, latest, texts.Select(t => new CommitmentDraft(t)).ToList());
        await db.SaveChangesAsync(cancellationToken);
        return texts.Count;
    }

    // ------------------------------------------------------------------ shared helpers

    /// <summary>Adds commitment rows for a check-in (no save).</summary>
    internal static void AddCommitments(LtfiDbContext db, ReflectionEntry checkIn, IReadOnlyList<CommitmentDraft> drafts)
    {
        var week = WeeklyCommitments.WeekOf(checkIn.CreatedAt);
        for (var i = 0; i < drafts.Count; i++)
        {
            db.Commitments.Add(new WeeklyCommitment
            {
                CheckInId = checkIn.Id,
                WeekStart = week,
                Text = drafts[i].Text,
                LinkedTaskId = drafts[i].LinkedTaskId,
                SortOrder = i,
                CreatedAt = checkIn.CreatedAt
            });
        }
    }

    /// <summary>
    /// Marks a tracked commitment Kept and adds its <see cref="EvidenceType.CommitmentKept"/> item
    /// unless one already exists for it (no save). Idempotent across calls.
    /// </summary>
    internal static async Task MarkKeptAsync(
        LtfiDbContext db, WeeklyCommitment commitment, DateTimeOffset at, Guid? projectId, CancellationToken cancellationToken)
    {
        commitment.Status = CommitmentStatus.Kept;
        commitment.ResolvedAt = at;

        var metadata = Metadata(commitment.Id);
        var exists = await db.Evidence.AnyAsync(
            e => e.Type == EvidenceType.CommitmentKept && e.MetadataJson == metadata, cancellationToken)
            || db.Evidence.Local.Any(e => e.Type == EvidenceType.CommitmentKept && e.MetadataJson == metadata);
        if (exists)
        {
            return;
        }

        db.Evidence.Add(new EvidenceItem
        {
            Type = EvidenceType.CommitmentKept,
            Source = WeeklyCommitments.EvidenceSource,
            Title = $"Kept: {commitment.Text}",
            ProjectId = projectId,
            TaskId = commitment.LinkedTaskId,
            OccurredAt = at,
            MetadataJson = metadata
        });
    }

    /// <summary>Open commitments whose linked task is Completed → Kept (saves when anything changed).</summary>
    internal static async Task ReconcileAsync(LtfiDbContext db, CancellationToken cancellationToken)
    {
        var open = await db.Commitments
            .Where(c => c.Status == CommitmentStatus.Open && c.LinkedTaskId != null)
            .ToListAsync(cancellationToken);
        if (open.Count == 0)
        {
            return;
        }

        var ids = open.Select(c => c.LinkedTaskId!.Value).Distinct().ToList();
        var done = await db.Tasks.AsNoTracking()
            .Where(t => ids.Contains(t.Id) && t.Status == TaskStatus.Completed)
            .Select(t => new { t.Id, t.ProjectId, t.CompletedAt })
            .ToDictionaryAsync(t => t.Id, cancellationToken);
        if (done.Count == 0)
        {
            return;
        }

        foreach (var c in open)
        {
            if (done.TryGetValue(c.LinkedTaskId!.Value, out var task))
            {
                await MarkKeptAsync(db, c, task.CompletedAt ?? DateTimeOffset.Now, task.ProjectId, cancellationToken);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string Metadata(Guid commitmentId) => $"{{\"commitmentId\":\"{commitmentId:D}\"}}";

    private static async Task<IReadOnlyList<CommitmentLine>> ToLinesAsync(
        LtfiDbContext db, List<WeeklyCommitment> rows, CancellationToken cancellationToken)
    {
        var ids = rows.Where(c => c.LinkedTaskId != null).Select(c => c.LinkedTaskId!.Value).Distinct().ToList();
        var tasks = ids.Count == 0
            ? new Dictionary<Guid, TaskItem>()
            : await db.Tasks.AsNoTracking()
                .Include(t => t.Area)
                .Where(t => ids.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, cancellationToken);

        return rows
            .OrderBy(c => c.WeekStart)
            .ThenBy(c => c.CreatedAt)
            .ThenBy(c => c.SortOrder)
            .Select(c =>
            {
                TaskItem? t = null;
                if (c.LinkedTaskId is { } tid)
                {
                    tasks.TryGetValue(tid, out t);
                }

                return new CommitmentLine(
                    c.Id, c.CheckInId, c.WeekStart, c.Text, c.Status, c.ResolvedAt, c.SortOrder,
                    c.LinkedTaskId, t?.Title, t?.Area?.Name, t?.DueAt,
                    t is not null && t.Status is not (TaskStatus.Completed or TaskStatus.Canceled) && t.ExternalRemovedAt is null);
            })
            .ToList();
    }
}
