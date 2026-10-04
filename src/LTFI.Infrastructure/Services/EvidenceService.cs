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
        var titles = await ProjectTitlesAsync(db, cancellationToken);

        return evidence
            .OrderByDescending(e => e.OccurredAt)
            .Take(limit)
            .Select(e => ToLine(e, titles))
            .ToList();
    }

    public async Task<IReadOnlyList<EvidenceLine>> GetForDayAsync(
        DateOnly day, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var evidence = await db.Evidence.AsNoTracking().ToListAsync(cancellationToken);
        var titles = await ProjectTitlesAsync(db, cancellationToken);

        // Same local-day bucketing as the contribution graph, so a cell and its feed agree.
        return evidence
            .Where(e => DateOnly.FromDateTime(e.OccurredAt.LocalDateTime) == day)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => ToLine(e, titles))
            .ToList();
    }

    private static Task<Dictionary<Guid, string>> ProjectTitlesAsync(LtfiDbContext db, CancellationToken cancellationToken) =>
        db.Projects.AsNoTracking()
            .Select(p => new { p.Id, p.Title })
            .ToDictionaryAsync(p => p.Id, p => p.Title, cancellationToken);

    private static EvidenceLine ToLine(EvidenceItem e, IReadOnlyDictionary<Guid, string> titles) =>
        new(e.Id, e.Type, e.Source, e.Title, e.Summary,
            e.ProjectId,
            e.ProjectId is { } pid && titles.TryGetValue(pid, out var title) ? title : null,
            e.OccurredAt);

    public async Task<IReadOnlyList<DayActivity>> GetDailyActivityAsync(
        int days, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var occurredAt = await db.Evidence.AsNoTracking()
            .Select(e => e.OccurredAt)
            .ToListAsync(cancellationToken);

        return Bucket(occurredAt, days);
    }

    public async Task<IReadOnlyList<DayScore>> GetDailyScoresAsync(
        int days, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Evidence.AsNoTracking()
            .Select(e => new { e.Type, e.OccurredAt })
            .ToListAsync(cancellationToken);

        days = Math.Max(1, days);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var start = today.AddDays(-(days - 1));

        // Only evidence that actually contributes (weight > 0) is counted as an event.
        var byDay = rows
            .Select(r => (Day: DateOnly.FromDateTime(r.OccurredAt.LocalDateTime), Weight: EvidencePoints.ForContribution(r.Type)))
            .Where(r => r.Weight > 0 && r.Day >= start && r.Day <= today)
            .GroupBy(r => r.Day)
            .ToDictionary(g => g.Key, g => (Points: g.Sum(r => r.Weight), Events: g.Count()));

        var result = new List<DayScore>(days);
        for (var i = 0; i < days; i++)
        {
            var day = start.AddDays(i);
            result.Add(byDay.TryGetValue(day, out var s)
                ? new DayScore(day, s.Points, s.Events)
                : new DayScore(day, 0, 0));
        }

        return result;
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
