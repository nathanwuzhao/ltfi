using System;
using System.Linq;
using LTFI.Core.Domain;
using Xunit;

namespace LTFI.Infrastructure.Tests;

public class ScoringTests
{
    [Theory]
    [InlineData(EvidenceType.TaskCompleted, 10)]
    [InlineData(EvidenceType.SubtaskCompleted, 2)]
    [InlineData(EvidenceType.FocusSessionCompleted, 5)]
    [InlineData(EvidenceType.ReflectionSubmitted, 10)]
    [InlineData(EvidenceType.GitCommit, 0)]
    public void Evidence_point_values(EvidenceType type, int expected) =>
        Assert.Equal(expected, EvidencePoints.For(type));

    [Fact]
    public void Streak_counts_consecutive_days_back_from_today()
    {
        var today = new DateOnly(2026, 6, 27);

        Assert.Equal(0, Streaks.ConsecutiveDays(Array.Empty<DateOnly>(), today));
        Assert.Equal(1, Streaks.ConsecutiveDays(new[] { today }, today));
        Assert.Equal(3, Streaks.ConsecutiveDays(
            new[] { today, today.AddDays(-1), today.AddDays(-2) }, today));
    }

    [Fact]
    public void Streak_survives_today_having_no_activity_yet()
    {
        var today = new DateOnly(2026, 6, 27);
        // Active yesterday and the day before, nothing today yet -> streak of 2 still stands.
        Assert.Equal(2, Streaks.ConsecutiveDays(
            new[] { today.AddDays(-1), today.AddDays(-2) }, today));
    }

    [Fact]
    public void Streak_breaks_on_a_gap()
    {
        var today = new DateOnly(2026, 6, 27);
        // Today and two days ago, but yesterday missing -> only today counts.
        Assert.Equal(1, Streaks.ConsecutiveDays(
            new[] { today, today.AddDays(-2) }, today));
    }
}
