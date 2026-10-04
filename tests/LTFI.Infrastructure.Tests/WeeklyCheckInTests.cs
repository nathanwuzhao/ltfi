using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using LTFI.Core.Domain;
using LTFI.Infrastructure.Services;
using Xunit;

namespace LTFI.Infrastructure.Tests;

/// <summary>Weekly check-in: the pure due/snooze rules plus the ReflectionService round trip.</summary>
public class WeeklyCheckInTests
{
    // 2026-10-03 is a Saturday; the week boundary before it is Sunday 2026-09-27 00:00.
    private static readonly DateTimeOffset Saturday = new(2026, 10, 3, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Sunday = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);

    private static string[] Answers(string commitments = "1. ship check-in — open the editor") =>
        ["done stuff", "", "keep", "energy", commitments, "start earlier"];

    [Fact]
    public void Questions_are_fixed_and_versioned()
    {
        Assert.Equal(6, WeeklyCheckIn.Questions.Count);
        Assert.Equal("weekly-checkin-v1", WeeklyCheckIn.PromptVersion);
        Assert.StartsWith("Top 3 commitments", WeeklyCheckIn.Questions[WeeklyCheckIn.CommitmentsQuestionIndex]);
    }

    [Fact]
    public void Week_start_is_the_most_recent_sunday_midnight()
    {
        Assert.Equal(Sunday, WeeklyCheckIn.WeekStart(Saturday));
        Assert.Equal(Sunday, WeeklyCheckIn.WeekStart(Sunday));                    // exactly on the boundary
        Assert.Equal(Sunday, WeeklyCheckIn.WeekStart(Sunday.AddHours(23)));
        Assert.Equal(Sunday.AddDays(-7), WeeklyCheckIn.WeekStart(Sunday.AddTicks(-1)));
    }

    [Fact]
    public void Due_when_no_check_in_since_the_boundary()
    {
        Assert.True(WeeklyCheckIn.IsDue(null, Saturday));                         // never checked in
        Assert.True(WeeklyCheckIn.IsDue(Sunday.AddMinutes(-1), Saturday));         // last one was last week
        Assert.False(WeeklyCheckIn.IsDue(Sunday.AddHours(9), Saturday));           // done this week
        Assert.True(WeeklyCheckIn.IsDue(Sunday.AddHours(9), Saturday.AddDays(1))); // a new week began
    }

    [Fact]
    public void Snooze_is_capped_per_week_and_resets_next_week()
    {
        var s1 = WeeklyCheckIn.Snooze(null, null, Saturday);
        Assert.Equal(1, s1.Count);
        Assert.Equal(Saturday.AddHours(ProjectPolicy.CheckInSnoozeHours), s1.Until);
        Assert.True(WeeklyCheckIn.IsSnoozed(s1, Saturday.AddHours(1)));
        Assert.False(WeeklyCheckIn.MustShow(null, s1, Saturday.AddHours(1)));
        Assert.True(WeeklyCheckIn.MustShow(null, s1, Saturday.AddHours(4)));       // snooze expired

        var s2 = WeeklyCheckIn.Snooze(s1, null, Saturday.AddHours(4));
        Assert.Equal(0, WeeklyCheckIn.SnoozesRemaining(s2, Saturday.AddHours(4)));
        Assert.Throws<InvalidOperationException>(() => WeeklyCheckIn.Snooze(s2, null, Saturday.AddHours(8)));

        // Next week the count starts over and an old snooze no longer suppresses the gate.
        var nextWeek = Saturday.AddDays(1).AddHours(1);
        Assert.Equal(ProjectPolicy.MaxCheckInSnoozesPerWeek, WeeklyCheckIn.SnoozesRemaining(s2, nextWeek));
        Assert.False(WeeklyCheckIn.IsSnoozed(s2, nextWeek));
    }

    [Fact]
    public void Cannot_snooze_when_nothing_is_due()
    {
        Assert.Throws<InvalidOperationException>(() =>
            WeeklyCheckIn.Snooze(null, Sunday.AddHours(1), Saturday));
    }

    [Fact]
    public void Answers_require_only_the_commitments()
    {
        Assert.Throws<InvalidOperationException>(() => WeeklyCheckIn.BuildAnswers(Answers(commitments: "  ")));
        Assert.Throws<ArgumentException>(() => WeeklyCheckIn.BuildAnswers(["just one"]));

        var built = WeeklyCheckIn.BuildAnswers(Answers());
        Assert.Equal(string.Empty, built[1].Answer);                               // others may be blank

        var roundTrip = WeeklyCheckIn.Deserialize(WeeklyCheckIn.Serialize(built));
        Assert.Equal(built, roundTrip);
        Assert.Contains("\"question\"", WeeklyCheckIn.Serialize(built));
        Assert.Empty(WeeklyCheckIn.Deserialize("not json"));
    }

    [Fact]
    public async Task Service_saves_check_in_writes_evidence_once_per_week_and_clears_due()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ltfi-test-{Guid.NewGuid():N}.db");
        var snoozePath = Path.Combine(Path.GetTempPath(), $"ltfi-snooze-{Guid.NewGuid():N}.json");
        try
        {
            var factory = new TestDbFactory(dbPath);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.MigrateAsync();
            }

            var clock = new FixedClock(Saturday);
            var service = new ReflectionService(factory, new JsonCheckInSnoozeStore(snoozePath), clock);

            var status = await service.GetWeeklyCheckInStatusAsync();
            Assert.True(status.MustShow);
            Assert.Equal(2, status.SnoozesRemaining);

            // Snooze persists through the JSON store (a fresh service sees it).
            await service.SnoozeWeeklyCheckInAsync();
            var reopened = new ReflectionService(factory, new JsonCheckInSnoozeStore(snoozePath), clock);
            var snoozed = await reopened.GetWeeklyCheckInStatusAsync();
            Assert.True(snoozed.IsSnoozed);
            Assert.False(snoozed.MustShow);
            Assert.Equal(1, snoozed.SnoozesRemaining);

            await service.SaveWeeklyCheckInAsync(Answers());
            clock.Now = Saturday.AddMinutes(5);
            await service.SaveWeeklyCheckInAsync(Answers("second pass"));

            var after = await service.GetWeeklyCheckInStatusAsync();
            Assert.False(after.IsDue);
            Assert.False(after.CanSnooze);

            var history = await service.GetWeeklyCheckInHistoryAsync();
            Assert.Equal(2, history.Count);
            Assert.Equal("second pass", history[0].Answers[WeeklyCheckIn.CommitmentsQuestionIndex].Answer);
            Assert.Equal(history[0].Id, (await service.GetLatestWeeklyCheckInAsync())!.Id);

            await using (var db = factory.CreateDbContext())
            {
                var entries = await db.Reflections.ToListAsync();
                Assert.All(entries, e => Assert.Equal(ReflectionScope.Week, e.ScopeType));
                Assert.All(entries, e => Assert.Equal(WeeklyCheckIn.PromptVersion, e.Prompt));

                var evidence = await db.Evidence.ToListAsync();
                var reflection = Assert.Single(evidence);                          // second save earns nothing
                Assert.Equal(EvidenceType.ReflectionSubmitted, reflection.Type);
                Assert.Equal(10, EvidencePoints.For(reflection.Type));
            }

            // Next week it falls due again.
            clock.Now = Saturday.AddDays(1).AddHours(1);
            Assert.True((await service.GetWeeklyCheckInStatusAsync()).MustShow);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { dbPath, dbPath + "-shm", dbPath + "-wal", snoozePath })
            {
                try { File.Delete(file); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>A settable clock pinned to UTC so local-time week math is deterministic.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
