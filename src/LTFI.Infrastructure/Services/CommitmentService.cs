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
/// those services and commitments. Reconcile also re-derives legacy (pre-2026-10-11, Sunday-week)
/// <see cref="WeeklyCommitment.WeekStart"/> values into the "week it applies to" model — an
/// idempotent data migration on read. The clock (and local zone) is injectable for tests.
/// </summary>
public sealed class CommitmentService(
    IDbContextFactory<LtfiDbContext> contextFactory,
    TimeProvider? clock = null,
    CheckInSchedule? schedule = null) : ICommitmentService
{
    private readonly IDbContextFactory<LtfiDbContext> _contextFactory = contextFactory;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly CheckInSchedule _schedule = schedule ?? CheckInSchedule.Default;

    private DateTimeOffset Now => _clock.GetLocalNow();

    private TimeZoneInfo Zone => _clock.LocalTimeZone;

    public async Task<IReadOnlyList<CommitmentLine>> GetCurrentWeekAsync(CancellationToken cancellationToken = default)
    {
        await BackfillCurrentWeekAsync(cancellationToken);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await ReconcileAsync(db, _schedule, Zone, cancellationToken);

        return await WeekLinesAsync(db, WeeklyCheckIn.MondayOf(Now), cancellationToken);
    }

    public async Task<CommitmentPanel> GetPanelAsync(CancellationToken cancellationToken = default)
    {
        await BackfillCurrentWeekAsync(cancellationToken);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await ReconcileAsync(db, _schedule, Zone, cancellationToken);

        // Once this week's own check-in is in (Sat/Sun window), it has settled this week's
        // commitments, so the panel moves on to the ones it just made for next week.
        var now = Now;
        var monday = WeeklyCheckIn.MondayOf(now);
        var state = WeeklyCheckIn.Evaluate(await ReflectionService.GetLatestLocalAsync(db, Zone, cancellationToken), now, _schedule);
        var isNext = state.Phase == CheckInPhase.Done && state.ReviewWeek == monday;
        var week = isNext ? monday.AddDays(7) : monday;

        return new CommitmentPanel(week, isNext, await WeekLinesAsync(db, week, cancellationToken));
    }

    private static async Task<IReadOnlyList<CommitmentLine>> WeekLinesAsync(LtfiDbContext db, DateOnly week, CancellationToken cancellationToken)
    {
        var rows = await db.Commitments.AsNoTracking()
            .Where(c => c.WeekStart == week && c.Status != CommitmentStatus.Dropped)
            .ToListAsync(cancellationToken);

        return await ToLinesAsync(db, rows, cancellationToken);
    }

    public async Task<IReadOnlyList<CommitmentLine>> GetPendingReviewAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await ReconcileAsync(db, _schedule, Zone, cancellationToken);

        // The week the check-in being written reviews: its commitments, plus older ones still Open
        // (a skipped review week leaves them open).
        var state = WeeklyCheckIn.Evaluate(await ReflectionService.GetLatestLocalAsync(db, Zone, cancellationToken), Now, _schedule);
        var week = state.ReviewWeek;
        var rows = await db.Commitments.AsNoTracking()
            .Where(c => c.Status != CommitmentStatus.Dropped
                        && (c.WeekStart == week || (c.WeekStart < week && c.Status == CommitmentStatus.Open)))
            .ToListAsync(cancellationToken);

        // Once the review week's check-in is in and nothing is left open, there is nothing to review.
        if (state.Phase == CheckInPhase.Done && rows.All(c => c.Status != CommitmentStatus.Open))
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

        // The check-ins whose commitments apply to this week (they reviewed last week) and to next
        // week (they reviewed this one): the latest of each, if it has no rows yet.
        var monday = WeeklyCheckIn.MondayOf(Now);
        var entries = await ReflectionService.WeeklyEntries(db).ToListAsync(cancellationToken);
        var latestPerWeek = entries
            .Select(r => (Entry: r, Reviewed: WeeklyCheckIn.ReviewedWeekOf(ReflectionService.ToLocal(r.CreatedAt, Zone), _schedule)))
            .Where(x => x.Reviewed == monday.AddDays(-7) || x.Reviewed == monday)
            .GroupBy(x => x.Reviewed)
            .Select(g => g.OrderByDescending(x => x.Entry.CreatedAt).First())
            .ToList();

        var created = 0;
        foreach (var (latest, reviewed) in latestPerWeek)
        {
            if (await db.Commitments.AnyAsync(c => c.CheckInId == latest.Id, cancellationToken))
            {
                continue;
            }

            var answers = WeeklyCheckIn.Deserialize(latest.Body);
            var q5 = answers.Count > WeeklyCheckIn.CommitmentsQuestionIndex
                ? answers[WeeklyCheckIn.CommitmentsQuestionIndex].Answer
                : null;
            var texts = WeeklyCommitments.SplitLegacyAnswer(q5);
            AddCommitments(db, latest, WeeklyCheckIn.CommitmentWeekFor(reviewed), texts.Select(t => new CommitmentDraft(t)).ToList());
            created += texts.Count;
        }

        if (created > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return created;
    }

    // ------------------------------------------------------------------ shared helpers

    /// <summary>Adds commitment rows for a check-in (no save); <paramref name="week"/> is the Monday they apply to.</summary>
    internal static void AddCommitments(LtfiDbContext db, ReflectionEntry checkIn, DateOnly week, IReadOnlyList<CommitmentDraft> drafts)
    {
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

    /// <summary>
    /// Data migration on read (2026-10-11): rows written under the old model hold the Sunday-start
    /// week their check-in fell in. Each such row gets the Monday of the week it applies to, derived
    /// from its check-in's time (<see cref="WeeklyCommitments.WeekFor"/>). New values are always
    /// Mondays and old ones never were, so this is idempotent and a no-op once done. Saves when
    /// anything changed; returns how many rows moved.
    /// </summary>
    internal static async Task<int> MigrateLegacyWeeksAsync(
        LtfiDbContext db, CheckInSchedule schedule, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var weeks = await db.Commitments.AsNoTracking()
            .Select(c => new { c.Id, c.WeekStart })
            .ToListAsync(cancellationToken);
        var legacyIds = weeks.Where(w => WeeklyCommitments.IsLegacyWeekStart(w.WeekStart)).Select(w => w.Id).ToList();
        if (legacyIds.Count == 0)
        {
            return 0;
        }

        var legacy = await db.Commitments.Where(c => legacyIds.Contains(c.Id)).ToListAsync(cancellationToken);
        var checkInIds = legacy.Select(c => c.CheckInId).Distinct().ToList();
        var checkInTimes = await db.Reflections.AsNoTracking()
            .Where(r => checkInIds.Contains(r.Id))
            .Select(r => new { r.Id, r.CreatedAt })
            .ToDictionaryAsync(r => r.Id, r => r.CreatedAt, cancellationToken);

        foreach (var c in legacy)
        {
            var at = checkInTimes.TryGetValue(c.CheckInId, out var t) ? t : c.CreatedAt;
            c.WeekStart = WeeklyCommitments.WeekFor(ReflectionService.ToLocal(at, zone), schedule);
        }

        await db.SaveChangesAsync(cancellationToken);
        return legacy.Count;
    }

    /// <summary>
    /// Re-derives legacy week values (<see cref="MigrateLegacyWeeksAsync"/>), then marks Open
    /// commitments whose linked task is Completed as Kept (saves when anything changed).
    /// </summary>
    internal static async Task ReconcileAsync(
        LtfiDbContext db, CheckInSchedule schedule, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        await MigrateLegacyWeeksAsync(db, schedule, zone, cancellationToken);

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
