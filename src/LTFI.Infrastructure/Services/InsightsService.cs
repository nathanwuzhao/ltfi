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
/// Derives the daily points total, the focus streak and the activity streak from evidence records.
/// The activity streak uses the contribution graph's own rule (<see cref="ContributionGraph.ScoreDays"/>
/// + <see cref="ContributionGraph.CurrentStreak"/>), so the header and the graph always agree.
/// </summary>
public sealed class InsightsService(IDbContextFactory<LtfiDbContext> contextFactory) : IInsightsService
{
    private readonly IDbContextFactory<LtfiDbContext> _contextFactory = contextFactory;

    public async Task<TodaySnapshot> GetTodaySnapshotAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var evidence = await db.Evidence.AsNoTracking().ToListAsync(cancellationToken);

        var today = DateOnly.FromDateTime(DateTime.Today);

        var pointsToday = EvidencePoints.Sum(
            evidence.Where(e => DateOnly.FromDateTime(e.OccurredAt.LocalDateTime) == today));

        var focusDays = evidence
            .Where(e => e.Type == EvidenceType.FocusSessionCompleted)
            .Select(e => DateOnly.FromDateTime(e.OccurredAt.LocalDateTime))
            .Distinct();

        return new TodaySnapshot(
            pointsToday,
            Streaks.ConsecutiveDays(focusDays, today),
            ActivityStreak(evidence.Select(e => (e.Type, e.OccurredAt)), today));
    }

    public async Task<int> GetActivityStreakAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Evidence.AsNoTracking()
            .Select(e => new { e.Type, e.OccurredAt })
            .ToListAsync(cancellationToken);

        return ActivityStreak(rows.Select(r => (r.Type, r.OccurredAt)), DateOnly.FromDateTime(DateTime.Today));
    }

    // Same window and scoring as the Command Center's graph (ContributionGraph.DefaultDays).
    private static int ActivityStreak(IEnumerable<(EvidenceType, DateTimeOffset)> evidence, DateOnly today) =>
        ContributionGraph.CurrentStreak(ContributionGraph.ScoreDays(evidence, today), today);
}
