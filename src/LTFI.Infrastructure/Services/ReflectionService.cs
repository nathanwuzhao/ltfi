using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using LTFI.Infrastructure.Persistence;

namespace LTFI.Infrastructure.Services;

/// <summary>
/// Persistence-backed <see cref="IReflectionService"/>. Weekly check-ins are <see cref="ReflectionEntry"/>
/// rows with <see cref="ReflectionScope.Week"/> and <see cref="WeeklyCheckIn.PromptVersion"/>; ordering
/// runs in memory because SQLite can't ORDER BY a DateTimeOffset. The clock is injectable for tests.
/// </summary>
public sealed class ReflectionService(
    IDbContextFactory<LtfiDbContext> contextFactory,
    ICheckInSnoozeStore snoozeStore,
    TimeProvider? clock = null) : IReflectionService
{
    private readonly IDbContextFactory<LtfiDbContext> _contextFactory = contextFactory;
    private readonly ICheckInSnoozeStore _snoozeStore = snoozeStore;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private DateTimeOffset Now => _clock.GetLocalNow();

    public Task<WeeklyCheckInRecord> SaveWeeklyCheckInAsync(
        IReadOnlyList<string?> answers, CancellationToken cancellationToken = default)
    {
        // Free-text form (v1 behaviour): Q5 is stored verbatim and its lines become the commitments.
        var built = WeeklyCheckIn.BuildAnswers(answers);
        var drafts = WeeklyCommitments.SplitLegacyAnswer(built[WeeklyCheckIn.CommitmentsQuestionIndex].Answer)
            .Select(t => new CommitmentDraft(t))
            .ToList();
        return SaveCoreAsync(built, drafts, null, cancellationToken);
    }

    public Task<WeeklyCheckInRecord> SaveWeeklyCheckInAsync(
        IReadOnlyList<string?> answers,
        IReadOnlyList<CommitmentDraft> commitments,
        IReadOnlyDictionary<Guid, CommitmentStatus>? resolutions = null,
        CancellationToken cancellationToken = default)
    {
        var drafts = WeeklyCommitments.Normalize(commitments);

        // Q5 is kept in the answers JSON as the joined commitments, for history and compatibility.
        var withQ5 = answers.ToArray();
        if (withQ5.Length == WeeklyCheckIn.Questions.Count)
        {
            withQ5[WeeklyCheckIn.CommitmentsQuestionIndex] = WeeklyCommitments.JoinForAnswer(drafts.Select(d => d.Text));
        }

        return SaveCoreAsync(WeeklyCheckIn.BuildAnswers(withQ5), drafts, resolutions, cancellationToken);
    }

    private async Task<WeeklyCheckInRecord> SaveCoreAsync(
        IReadOnlyList<WeeklyCheckInAnswer> built,
        IReadOnlyList<CommitmentDraft> drafts,
        IReadOnlyDictionary<Guid, CommitmentStatus>? resolutions,
        CancellationToken cancellationToken)
    {
        var now = Now;

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Only the first check-in of a week earns evidence/points, so re-submitting can't farm them.
        var lastBefore = await GetLatestCreatedAtAsync(db, cancellationToken);
        var firstThisWeek = WeeklyCheckIn.IsDue(lastBefore, now);

        // Linked tasks completed since the last read count as kept before anything is settled.
        await CommitmentService.ReconcileAsync(db, cancellationToken);

        var entry = new ReflectionEntry
        {
            ScopeType = ReflectionScope.Week,
            Prompt = WeeklyCheckIn.PromptVersion,
            Body = WeeklyCheckIn.Serialize(built),
            CreatedAt = now
        };
        db.Reflections.Add(entry);

        // Drop links to tasks that don't exist (stale picker).
        var linkIds = drafts.Where(d => d.LinkedTaskId != null).Select(d => d.LinkedTaskId!.Value).ToList();
        var existing = linkIds.Count == 0
            ? new HashSet<Guid>()
            : (await db.Tasks.Where(t => linkIds.Contains(t.Id)).Select(t => t.Id).ToListAsync(cancellationToken)).ToHashSet();
        CommitmentService.AddCommitments(db, entry, drafts
            .Select(d => d.LinkedTaskId is { } id && !existing.Contains(id) ? d with { LinkedTaskId = null } : d)
            .ToList());

        // Settle the commitments still open: earlier weeks → Kept (if chosen) or Missed; an earlier
        // check-in this same week → Dropped (superseded by this one).
        var week = WeeklyCommitments.WeekOf(now);
        var open = await db.Commitments
            .Where(c => c.Status == CommitmentStatus.Open)
            .ToListAsync(cancellationToken);
        foreach (var c in open.Where(c => c.CheckInId != entry.Id))
        {
            if (c.WeekStart >= week)
            {
                c.Status = CommitmentStatus.Dropped;
                c.ResolvedAt = now;
            }
            else if (resolutions is not null
                     && resolutions.TryGetValue(c.Id, out var choice)
                     && choice == CommitmentStatus.Kept)
            {
                var projectId = c.LinkedTaskId is { } tid
                    ? await db.Tasks.Where(t => t.Id == tid).Select(t => t.ProjectId).FirstOrDefaultAsync(cancellationToken)
                    : null;
                await CommitmentService.MarkKeptAsync(db, c, now, projectId, cancellationToken);
            }
            else
            {
                c.Status = CommitmentStatus.Missed;
                c.ResolvedAt = now;
            }
        }

        if (firstThisWeek)
        {
            db.Evidence.Add(new EvidenceItem
            {
                Type = EvidenceType.ReflectionSubmitted,
                Source = "reflection",
                Title = "Weekly check-in",
                Summary = built[WeeklyCheckIn.CommitmentsQuestionIndex].Answer,
                OccurredAt = now
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToRecord(entry);
    }

    public async Task<WeeklyCheckInRecord?> GetLatestWeeklyCheckInAsync(CancellationToken cancellationToken = default) =>
        (await GetWeeklyCheckInHistoryAsync(1, cancellationToken)).FirstOrDefault();

    public async Task<IReadOnlyList<WeeklyCheckInRecord>> GetWeeklyCheckInHistoryAsync(
        int limit = 10, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var entries = await WeeklyEntries(db).ToListAsync(cancellationToken);

        return entries
            .OrderByDescending(r => r.CreatedAt)
            .Take(limit)
            .Select(ToRecord)
            .ToList();
    }

    public async Task<WeeklyCheckInStatus> GetWeeklyCheckInStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var last = await GetLatestCreatedAtAsync(db, cancellationToken);
        return BuildStatus(last, _snoozeStore.Load(), Now);
    }

    public async Task<WeeklyCheckInStatus> SnoozeWeeklyCheckInAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var last = await GetLatestCreatedAtAsync(db, cancellationToken);
        var now = Now;

        var next = WeeklyCheckIn.Snooze(_snoozeStore.Load(), last, now);
        _snoozeStore.Save(next);
        return BuildStatus(last, next, now);
    }

    private static WeeklyCheckInStatus BuildStatus(DateTimeOffset? last, CheckInSnoozeState? snooze, DateTimeOffset now)
    {
        var snoozed = WeeklyCheckIn.IsSnoozed(snooze, now);
        return new WeeklyCheckInStatus(
            IsDue: WeeklyCheckIn.IsDue(last, now),
            IsSnoozed: snoozed,
            SnoozedUntil: snoozed ? snooze!.Until : null,
            SnoozesRemaining: WeeklyCheckIn.SnoozesRemaining(snooze, now),
            LastCheckInAt: last,
            NextDueAt: WeeklyCheckIn.WeekStart(now).AddDays(7));
    }

    private static IQueryable<ReflectionEntry> WeeklyEntries(LtfiDbContext db) =>
        db.Reflections.AsNoTracking()
            .Where(r => r.ScopeType == ReflectionScope.Week && r.Prompt == WeeklyCheckIn.PromptVersion);

    private static async Task<DateTimeOffset?> GetLatestCreatedAtAsync(LtfiDbContext db, CancellationToken cancellationToken)
    {
        var times = await WeeklyEntries(db).Select(r => r.CreatedAt).ToListAsync(cancellationToken);
        return times.Count == 0 ? null : times.Max();
    }

    /// <summary>True when a weekly check-in was saved at or after <paramref name="since"/>.</summary>
    internal static async Task<bool> HasCheckInSinceAsync(LtfiDbContext db, DateTimeOffset since, CancellationToken cancellationToken) =>
        await GetLatestCreatedAtAsync(db, cancellationToken) is { } last && last >= since;

    private static WeeklyCheckInRecord ToRecord(ReflectionEntry entry) =>
        new(entry.Id, entry.CreatedAt, WeeklyCheckIn.Deserialize(entry.Body));
}
