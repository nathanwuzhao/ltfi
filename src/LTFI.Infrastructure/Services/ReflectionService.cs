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
/// runs in memory because SQLite can't ORDER BY a DateTimeOffset. The week a check-in reviews is
/// never stored: it is derived from <see cref="ReflectionEntry.CreatedAt"/> (converted to the local
/// zone) by <see cref="WeeklyCheckIn.ReviewedWeekOf"/> and the configured <see cref="CheckInSchedule"/>.
/// The clock (and so the local zone) is injectable for tests.
/// </summary>
public sealed class ReflectionService(
    IDbContextFactory<LtfiDbContext> contextFactory,
    ICheckInSnoozeStore snoozeStore,
    TimeProvider? clock = null,
    CheckInSchedule? schedule = null) : IReflectionService
{
    private readonly IDbContextFactory<LtfiDbContext> _contextFactory = contextFactory;
    private readonly ICheckInSnoozeStore _snoozeStore = snoozeStore;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly CheckInSchedule _schedule = schedule ?? CheckInSchedule.Default;

    private DateTimeOffset Now => _clock.GetLocalNow();

    private TimeZoneInfo Zone => _clock.LocalTimeZone;

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
        var reviewed = WeeklyCheckIn.ReviewedWeekOf(now, _schedule);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Only the first check-in for a reviewed week earns evidence/points, so re-submitting can't farm them.
        var earlier = await WeeklyEntries(db).Select(r => r.CreatedAt).ToListAsync(cancellationToken);
        var firstForWeek = !earlier.Any(t => WeeklyCheckIn.ReviewedWeekOf(ToLocal(t, Zone), _schedule) == reviewed);

        // Legacy week values are re-derived and linked tasks completed since the last read count as
        // kept, before anything is settled.
        await CommitmentService.ReconcileAsync(db, _schedule, Zone, cancellationToken);

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
        CommitmentService.AddCommitments(db, entry, WeeklyCheckIn.CommitmentWeekFor(reviewed), drafts
            .Select(d => d.LinkedTaskId is { } id && !existing.Contains(id) ? d with { LinkedTaskId = null } : d)
            .ToList());

        // Settle the commitments still open: those that applied to the reviewed week or earlier →
        // Kept (if chosen) or Missed; those made by an earlier check-in for this same reviewed week
        // (they apply to next week) → Dropped, superseded by this one.
        var open = await db.Commitments
            .Where(c => c.Status == CommitmentStatus.Open)
            .ToListAsync(cancellationToken);
        foreach (var c in open.Where(c => c.CheckInId != entry.Id))
        {
            if (c.WeekStart > reviewed)
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

        if (firstForWeek)
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
        var last = await GetLatestLocalAsync(db, Zone, cancellationToken);
        return BuildStatus(last, _snoozeStore.Load(), Now);
    }

    public async Task<WeeklyCheckInStatus> SnoozeWeeklyCheckInAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var last = await GetLatestLocalAsync(db, Zone, cancellationToken);
        var now = Now;

        var next = WeeklyCheckIn.Snooze(_snoozeStore.Load(), last, now, _schedule);
        _snoozeStore.Save(next);
        return BuildStatus(last, next, now);
    }

    private WeeklyCheckInStatus BuildStatus(DateTimeOffset? last, CheckInSnoozeState? snooze, DateTimeOffset now)
    {
        var state = WeeklyCheckIn.Evaluate(last, now, _schedule);
        var snoozed = WeeklyCheckIn.IsSnoozed(snooze, now, _schedule);
        return new WeeklyCheckInStatus(
            Phase: state.Phase,
            ReviewWeek: state.ReviewWeek,
            IsSnoozed: snoozed,
            SnoozedUntil: snoozed ? snooze!.Until : null,
            SnoozesRemaining: WeeklyCheckIn.SnoozesRemaining(snooze, now, _schedule),
            SnoozeHours: _schedule.SnoozeHours,
            LastCheckInAt: last,
            OpensAt: state.OpensAt,
            GateAt: state.GateAt,
            DueAt: state.DueAt,
            NextOpensAt: state.NextOpensAt,
            CanSubmit: state.CanSubmit);
    }

    internal static IQueryable<ReflectionEntry> WeeklyEntries(LtfiDbContext db) =>
        db.Reflections.AsNoTracking()
            .Where(r => r.ScopeType == ReflectionScope.Week && r.Prompt == WeeklyCheckIn.PromptVersion);

    /// <summary>A stored time on the local wall clock (week math is wall-clock based).</summary>
    internal static DateTimeOffset ToLocal(DateTimeOffset at, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(at, zone);

    /// <summary>The most recent weekly check-in's time, in the local zone; null when there is none.</summary>
    internal static async Task<DateTimeOffset?> GetLatestLocalAsync(LtfiDbContext db, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var times = await WeeklyEntries(db).Select(r => r.CreatedAt).ToListAsync(cancellationToken);
        return times.Count == 0 ? null : ToLocal(times.Max(), zone);
    }

    private WeeklyCheckInRecord ToRecord(ReflectionEntry entry)
    {
        var local = ToLocal(entry.CreatedAt, Zone);
        return new(entry.Id, entry.CreatedAt, WeeklyCheckIn.Deserialize(entry.Body),
            WeeklyCheckIn.ReviewedWeekOf(local, _schedule), WeeklyCheckIn.IsLate(local, _schedule));
    }
}
