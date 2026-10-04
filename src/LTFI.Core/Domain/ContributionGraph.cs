using System;
using System.Collections.Generic;
using System.Linq;

namespace LTFI.Core.Domain;

/// <summary>Weighted contribution points and contributing-event count for one local day.</summary>
public sealed record DayScore(DateOnly Day, int Points, int Events);

/// <summary>Where a grid cell sits relative to the trailing window.</summary>
public enum ContributionCellKind
{
    /// <summary>A real day inside the window (may still have zero points).</summary>
    Day,

    /// <summary>Leading padding in the first column, before the window starts.</summary>
    BeforeRange,

    /// <summary>Trailing padding in the last column, after today.</summary>
    Future
}

/// <summary>One square of the graph. <see cref="Level"/> is 0 (empty) to 4 (densest).</summary>
public sealed record ContributionCell(DateOnly Day, int Points, int Events, int Level, ContributionCellKind Kind);

/// <summary>
/// One column of the graph: Sunday → Saturday. <see cref="MonthLabel"/> is the month number (1–12)
/// when this column starts a new month and should carry a label, otherwise null.
/// </summary>
public sealed record ContributionWeek(DateOnly WeekStart, IReadOnlyList<ContributionCell> Days, int? MonthLabel);

/// <summary>Headline numbers shown beside the graph.</summary>
public sealed record ContributionStats(
    int TotalPoints,
    int TotalEvents,
    int ActiveDays,
    int CurrentStreak,
    int LongestStreak,
    DayScore? BestDay);

/// <summary>
/// A GitHub-style contribution graph (the Command Center's headline panel): the trailing
/// <c>days</c> window ending today, laid out as Sunday-start week columns × 7 weekday rows.
/// Intensity levels follow GitHub's approach — quartiles of the non-zero days — so the ramp
/// adapts to however much the user actually does, and the best day is always the brightest.
/// Pure: callers supply per-day scores and "today".
/// </summary>
public sealed record ContributionGraph(
    IReadOnlyList<ContributionWeek> Weeks,
    ContributionStats Stats,
    DateOnly Start,
    DateOnly Today)
{
    public const int DefaultDays = 365;

    /// <summary>Months closer than this many columns to the next label are left unlabelled
    /// (stops a partial first column's label colliding with the next month's).</summary>
    private const int MinLabelGapWeeks = 3;

    public static ContributionGraph Build(IEnumerable<DayScore> scores, DateOnly today, int days = DefaultDays)
    {
        days = Math.Max(1, days);
        var start = today.AddDays(-(days - 1));

        // Keep only in-window days; tolerate duplicates by summing them.
        var byDay = scores
            .Where(s => s.Day >= start && s.Day <= today)
            .GroupBy(s => s.Day)
            .ToDictionary(
                g => g.Key,
                g => new DayScore(g.Key, g.Sum(s => s.Points), g.Sum(s => s.Events)));

        var thresholds = Thresholds(byDay.Values.Select(s => s.Points));

        // Grid: from the Sunday on/before start to the Saturday on/after today.
        var gridStart = start.AddDays(-(int)start.DayOfWeek);
        var gridEnd = today.AddDays(6 - (int)today.DayOfWeek);

        var weeks = new List<ContributionWeek>();
        for (var weekStart = gridStart; weekStart <= gridEnd; weekStart = weekStart.AddDays(7))
        {
            var cells = new List<ContributionCell>(7);
            for (var d = 0; d < 7; d++)
            {
                var day = weekStart.AddDays(d);
                if (day < start)
                {
                    cells.Add(new ContributionCell(day, 0, 0, 0, ContributionCellKind.BeforeRange));
                }
                else if (day > today)
                {
                    cells.Add(new ContributionCell(day, 0, 0, 0, ContributionCellKind.Future));
                }
                else
                {
                    byDay.TryGetValue(day, out var s);
                    var points = s?.Points ?? 0;
                    cells.Add(new ContributionCell(day, points, s?.Events ?? 0, LevelFor(points, thresholds), ContributionCellKind.Day));
                }
            }

            weeks.Add(new ContributionWeek(weekStart, cells, null));
        }

        return new ContributionGraph(LabelMonths(weeks), ComputeStats(byDay.Values, start, today), start, today);
    }

    /// <summary>
    /// Quartile cut points (nearest-rank 25th/50th/75th percentile) plus the max of the
    /// non-zero values. Empty when there is no activity.
    /// </summary>
    public static IReadOnlyList<int> Thresholds(IEnumerable<int> points)
    {
        var sorted = points.Where(p => p > 0).OrderBy(p => p).ToArray();
        if (sorted.Length == 0)
        {
            return Array.Empty<int>();
        }

        int Rank(double q) => sorted[Math.Max(0, (int)Math.Ceiling(q * sorted.Length) - 1)];
        return new[] { Rank(0.25), Rank(0.5), Rank(0.75), sorted[^1] };
    }

    /// <summary>0 for no activity; the window's best value is always 4; otherwise the quartile band.</summary>
    public static int LevelFor(int points, IReadOnlyList<int> thresholds)
    {
        if (points <= 0 || thresholds.Count < 4)
        {
            return 0;
        }

        if (points >= thresholds[3]) return 4;
        if (points <= thresholds[0]) return 1;
        if (points <= thresholds[1]) return 2;
        if (points <= thresholds[2]) return 3;
        return 4;
    }

    private static ContributionStats ComputeStats(IEnumerable<DayScore> scores, DateOnly start, DateOnly today)
    {
        var active = scores.Where(s => s.Points > 0).ToList();
        var activeDays = active.Select(s => s.Day).ToHashSet();

        // Longest run of consecutive active days inside the window.
        var longest = 0;
        var run = 0;
        for (var day = start; day <= today; day = day.AddDays(1))
        {
            run = activeDays.Contains(day) ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        // Best day: most points, latest day wins ties (the most recent personal best).
        var best = active
            .OrderByDescending(s => s.Points)
            .ThenByDescending(s => s.Day)
            .FirstOrDefault();

        return new ContributionStats(
            TotalPoints: active.Sum(s => s.Points),
            TotalEvents: active.Sum(s => s.Events),
            ActiveDays: activeDays.Count,
            CurrentStreak: Streaks.ConsecutiveDays(activeDays, today),
            LongestStreak: longest,
            BestDay: best);
    }

    /// <summary>
    /// A column carries a month label when its first in-window day falls in a different month
    /// from the previous column's. A label is dropped if the next label is fewer than
    /// <see cref="MinLabelGapWeeks"/> columns away, so labels never overlap.
    /// </summary>
    private static IReadOnlyList<ContributionWeek> LabelMonths(List<ContributionWeek> weeks)
    {
        var starts = new List<int>();
        int? previousMonth = null;
        for (var i = 0; i < weeks.Count; i++)
        {
            var first = weeks[i].Days.FirstOrDefault(c => c.Kind == ContributionCellKind.Day);
            if (first is null)
            {
                continue;
            }

            if (first.Day.Month != previousMonth)
            {
                starts.Add(i);
                previousMonth = first.Day.Month;
            }
        }

        var result = weeks.ToList();
        for (var k = 0; k < starts.Count; k++)
        {
            var i = starts[k];
            var tooClose = k + 1 < starts.Count && starts[k + 1] - i < MinLabelGapWeeks;
            if (tooClose)
            {
                continue;
            }

            var month = result[i].Days.First(c => c.Kind == ContributionCellKind.Day).Day.Month;
            result[i] = result[i] with { MonthLabel = month };
        }

        return result;
    }
}
