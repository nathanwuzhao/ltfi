using System;
using System.Linq;
using LTFI.Core.Domain;
using Xunit;

namespace LTFI.Infrastructure.Tests;

public class ContributionGraphTests
{
    [Theory]
    [InlineData(2026, 10, 3)]  // Saturday: last column full, no future cells
    [InlineData(2026, 9, 30)]  // Wednesday: three future cells
    [InlineData(2026, 10, 4)]  // Sunday: last column is just today
    public void Grid_is_sunday_aligned_with_today_in_the_last_column(int y, int m, int d)
    {
        var today = new DateOnly(y, m, d);
        var g = ContributionGraph.Build(Array.Empty<DayScore>(), today);

        Assert.All(g.Weeks, w =>
        {
            Assert.Equal(DayOfWeek.Sunday, w.WeekStart.DayOfWeek);
            Assert.Equal(7, w.Days.Count);
            for (var i = 0; i < 7; i++) Assert.Equal(w.WeekStart.AddDays(i), w.Days[i].Day);
        });

        // Exactly the 365-day window is real; padding before it and future cells after today.
        var cells = g.Weeks.SelectMany(w => w.Days).ToList();
        Assert.Equal(365, cells.Count(c => c.Kind == ContributionCellKind.Day));
        Assert.Equal(today.AddDays(-364), g.Start);
        Assert.Equal((int)g.Start.DayOfWeek, cells.Count(c => c.Kind == ContributionCellKind.BeforeRange));

        var last = g.Weeks[^1];
        var todayIndex = (int)today.DayOfWeek;
        Assert.Equal(today, last.Days[todayIndex].Day);
        Assert.Equal(ContributionCellKind.Day, last.Days[todayIndex].Kind);
        Assert.Equal(6 - todayIndex, cells.Count(c => c.Kind == ContributionCellKind.Future));
        Assert.All(last.Days.Skip(todayIndex + 1), c => Assert.Equal(ContributionCellKind.Future, c.Kind));
        Assert.All(cells.Where(c => c.Kind != ContributionCellKind.Day), c => Assert.Equal(0, c.Level));

        Assert.InRange(g.Weeks.Count, 53, 54);
    }

    [Fact]
    public void Levels_follow_quartiles_of_nonzero_days_and_best_day_is_brightest()
    {
        var t = ContributionGraph.Thresholds(new[] { 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.Equal(new[] { 2, 4, 6, 8 }, t);

        Assert.Equal(0, ContributionGraph.LevelFor(0, t));
        Assert.Equal(1, ContributionGraph.LevelFor(1, t));
        Assert.Equal(1, ContributionGraph.LevelFor(2, t));
        Assert.Equal(2, ContributionGraph.LevelFor(4, t));
        Assert.Equal(3, ContributionGraph.LevelFor(6, t));
        Assert.Equal(4, ContributionGraph.LevelFor(7, t));
        Assert.Equal(4, ContributionGraph.LevelFor(8, t));

        // A single active day (or all-equal days) is still the brightest level.
        var single = ContributionGraph.Thresholds(new[] { 5 });
        Assert.Equal(4, ContributionGraph.LevelFor(5, single));

        Assert.Empty(ContributionGraph.Thresholds(new[] { 0, 0 }));
        Assert.Equal(0, ContributionGraph.LevelFor(3, ContributionGraph.Thresholds(Array.Empty<int>())));
    }

    [Fact]
    public void Stats_count_totals_streaks_and_best_day()
    {
        var today = new DateOnly(2026, 10, 3);
        var scores = new[]
        {
            new DayScore(today, 10, 1),
            new DayScore(today.AddDays(-1), 5, 1),
            // gap at -2
            new DayScore(today.AddDays(-10), 2, 1),
            new DayScore(today.AddDays(-11), 2, 1),
            new DayScore(today.AddDays(-12), 25, 3),
            new DayScore(today.AddDays(-13), 2, 1),
            new DayScore(today.AddDays(-14), 2, 1),
            new DayScore(today.AddDays(-400), 99, 9), // outside the window
        };

        var g = ContributionGraph.Build(scores, today);

        Assert.Equal(48, g.Stats.TotalPoints);
        Assert.Equal(9, g.Stats.TotalEvents);
        Assert.Equal(7, g.Stats.ActiveDays);
        Assert.Equal(2, g.Stats.CurrentStreak);
        Assert.Equal(5, g.Stats.LongestStreak);
        Assert.Equal(today.AddDays(-12), g.Stats.BestDay?.Day);
        Assert.Equal(25, g.Stats.BestDay?.Points);

        var best = g.Weeks.SelectMany(w => w.Days).Single(c => c.Day == today.AddDays(-12));
        Assert.Equal(4, best.Level);
        Assert.Equal(3, best.Events);
    }

    [Fact]
    public void Empty_history_has_zero_stats()
    {
        var g = ContributionGraph.Build(Array.Empty<DayScore>(), new DateOnly(2026, 10, 3));

        Assert.Equal(0, g.Stats.TotalPoints);
        Assert.Equal(0, g.Stats.ActiveDays);
        Assert.Equal(0, g.Stats.CurrentStreak);
        Assert.Equal(0, g.Stats.LongestStreak);
        Assert.Null(g.Stats.BestDay);
        Assert.All(g.Weeks.SelectMany(w => w.Days), c => Assert.Equal(0, c.Level));
    }

    [Fact]
    public void Month_labels_mark_month_changes_without_crowding()
    {
        var g = ContributionGraph.Build(Array.Empty<DayScore>(), new DateOnly(2026, 10, 3));

        var labelled = g.Weeks
            .Select((w, i) => (w, i))
            .Where(x => x.w.MonthLabel is not null)
            .ToList();

        Assert.InRange(labelled.Count, 12, 13);
        for (var k = 1; k < labelled.Count; k++)
        {
            Assert.True(labelled[k].i - labelled[k - 1].i >= 3);
        }

        // Each label names the month of its column's first real day. Oct 3 sits in the column
        // starting Sun Sep 27, so October only gets a label once a column starts in it (Oct 4).
        Assert.All(labelled, x => Assert.Equal(
            x.w.Days.First(c => c.Kind == ContributionCellKind.Day).Day.Month, x.w.MonthLabel));
        Assert.Equal(9, labelled[^1].w.MonthLabel);

        var next = ContributionGraph.Build(Array.Empty<DayScore>(), new DateOnly(2026, 10, 4));
        Assert.Equal(10, next.Weeks[^1].MonthLabel);
    }

    [Theory]
    [InlineData(EvidenceType.TaskCompleted, 10)]
    [InlineData(EvidenceType.FocusSessionCompleted, 5)]
    [InlineData(EvidenceType.GitCommit, 1)]
    [InlineData(EvidenceType.ManualNote, 1)]
    [InlineData(EvidenceType.DistractionOverride, 0)]
    public void Contribution_weights_floor_unscored_evidence_at_one(EvidenceType type, int expected) =>
        Assert.Equal(expected, EvidencePoints.ForContribution(type));
}
