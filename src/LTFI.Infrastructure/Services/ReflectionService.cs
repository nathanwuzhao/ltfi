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

    public async Task<WeeklyCheckInRecord> SaveWeeklyCheckInAsync(
        IReadOnlyList<string?> answers, CancellationToken cancellationToken = default)
    {
        var built = WeeklyCheckIn.BuildAnswers(answers);
        var now = Now;

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Only the first check-in of a week earns evidence/points, so re-submitting can't farm them.
        var lastBefore = await GetLatestCreatedAtAsync(db, cancellationToken);
        var firstThisWeek = WeeklyCheckIn.IsDue(lastBefore, now);

        var entry = new ReflectionEntry
        {
            ScopeType = ReflectionScope.Week,
            Prompt = WeeklyCheckIn.PromptVersion,
            Body = WeeklyCheckIn.Serialize(built),
            CreatedAt = now
        };
        db.Reflections.Add(entry);

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

    private static WeeklyCheckInRecord ToRecord(ReflectionEntry entry) =>
        new(entry.Id, entry.CreatedAt, WeeklyCheckIn.Deserialize(entry.Body));
}
