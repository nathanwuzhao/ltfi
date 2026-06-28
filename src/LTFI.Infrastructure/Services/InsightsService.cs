using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using LTFI.Infrastructure.Persistence;

namespace LTFI.Infrastructure.Services;

/// <summary>Derives the daily points total and focus streak from evidence records.</summary>
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

        return new TodaySnapshot(pointsToday, Streaks.ConsecutiveDays(focusDays, today));
    }
}
