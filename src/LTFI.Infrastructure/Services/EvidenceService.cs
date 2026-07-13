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
/// Read-only queries over persisted <see cref="EvidenceItem"/> rows. Consistent with the other
/// services, rows are pulled and then ordered/filtered/grouped in memory (SQLite can't
/// range/order on <see cref="DateTimeOffset"/>); fine at personal scale.
/// </summary>
public sealed class EvidenceService(IDbContextFactory<LtfiDbContext> contextFactory) : IEvidenceService
{
    private readonly IDbContextFactory<LtfiDbContext> _contextFactory = contextFactory;

    public async Task<IReadOnlyList<EvidenceLine>> GetRecentAsync(
        int limit = 40, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var evidence = await db.Evidence.AsNoTracking().ToListAsync(cancellationToken);
        var titles = await db.Projects.AsNoTracking()
            .Select(p => new { p.Id, p.Title })
            .ToDictionaryAsync(p => p.Id, p => p.Title, cancellationToken);

        return evidence
            .OrderByDescending(e => e.OccurredAt)
            .Take(limit)
            .Select(e => new EvidenceLine(
                e.Id, e.Type, e.Source, e.Title, e.Summary,
                e.ProjectId,
                e.ProjectId is { } pid && titles.TryGetValue(pid, out var title) ? title : null,
                e.OccurredAt))
            .ToList();
    }

    public async Task<IReadOnlyList<DayActivity>> GetDailyActivityAsync(
        int days, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var occurredAt = await db.Evidence.AsNoTracking()
            .Select(e => e.OccurredAt)
            .ToListAsync(cancellationToken);

        return Bucket(occurredAt, days);
    }

    public async Task<IReadOnlyList<DayActivity>> GetProjectDailyActivityAsync(
        Guid projectId, int days, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var occurredAt = await db.Evidence.AsNoTracking()
            .Where(e => e.ProjectId == projectId)
            .Select(e => e.OccurredAt)
            .ToListAsync(cancellationToken);

        return Bucket(occurredAt, days);
    }

    /// <summary>Counts events per local day over the trailing window, emitting one entry per day
    /// (including zeros) oldest-first so callers can render a fixed-width strip.</summary>
    private static IReadOnlyList<DayActivity> Bucket(IEnumerable<DateTimeOffset> occurredAt, int days)
    {
        days = Math.Max(1, days);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var start = today.AddDays(-(days - 1));

        var counts = occurredAt
            .Select(o => DateOnly.FromDateTime(o.LocalDateTime))
            .Where(d => d >= start && d <= today)
            .GroupBy(d => d)
            .ToDictionary(g => g.Key, g => g.Count());

        var result = new List<DayActivity>(days);
        for (var i = 0; i < days; i++)
        {
            var day = start.AddDays(i);
            result.Add(new DayActivity(day, counts.TryGetValue(day, out var c) ? c : 0));
        }

        return result;
    }
}
