using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using LTFI.Infrastructure.Services;
using Xunit;

namespace LTFI.Infrastructure.Tests;

/// <summary>A hand-cranked clock for timer tests.</summary>
internal sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();

    public void Advance(TimeSpan by) => _now += by;
}

public class PomodoroNsdrTests
{
    // ---------- pure pomodoro state machine ----------

    [Fact]
    public void Work_then_short_break_and_back()
    {
        var c = PomodoroCycle.Start();
        Assert.Equal(PomodoroPhase.Work, c.Phase);
        Assert.Equal(0, c.CompletedWork);

        c = c.CompleteWork();
        Assert.Equal(PomodoroPhase.ShortBreak, c.Phase);
        Assert.Equal(1, c.CompletedWork);
        Assert.Equal("●○○○", c.Dots);

        c = c.EndBreak();
        Assert.Equal(PomodoroPhase.Work, c.Phase);
        Assert.Equal(1, c.CompletedWork);
    }

    [Fact]
    public void Every_fourth_work_interval_earns_a_long_break()
    {
        var c = PomodoroCycle.Start();
        for (var i = 0; i < 3; i++)
        {
            c = c.CompleteWork();
            Assert.Equal(PomodoroPhase.ShortBreak, c.Phase);
            c = c.SkipBreak();
        }

        c = c.CompleteWork();
        Assert.Equal(PomodoroPhase.LongBreak, c.Phase);
        Assert.Equal(4, c.CompletedWork);
        Assert.Equal(4, c.DotsFilled);

        // Next cycle starts empty, and the 8th interval is long again.
        c = c.EndBreak();
        Assert.Equal(0, c.DotsFilled);
        for (var i = 0; i < 3; i++) c = c.CompleteWork().EndBreak();
        Assert.Equal(PomodoroPhase.LongBreak, c.CompleteWork().Phase);
    }

    [Fact]
    public void Skip_break_keeps_count_and_nsdr_only_replaces_a_long_break()
    {
        var c = PomodoroCycle.Start().CompleteWork();
        Assert.Throws<InvalidOperationException>(() => c.TakeNsdrInstead());
        Assert.Throws<InvalidOperationException>(() => PomodoroCycle.Start().EndBreak());
        Assert.Throws<InvalidOperationException>(() => c.CompleteWork());

        var skipped = c.SkipBreak();
        Assert.Equal(PomodoroPhase.Work, skipped.Phase);
        Assert.Equal(1, skipped.CompletedWork);

        var longBreak = new PomodoroCycle(PomodoroPhase.Work, 3).CompleteWork();
        var nsdr = longBreak.TakeNsdrInstead();
        Assert.Equal(PomodoroPhase.Nsdr, nsdr.Phase);
        Assert.Equal(PomodoroPhase.Work, nsdr.EndBreak().Phase);
        Assert.Equal(4, nsdr.EndBreak().CompletedWork);
    }

    [Fact]
    public void Phase_durations()
    {
        Assert.Equal(TimeSpan.FromMinutes(25), Pomodoro.DurationOf(PomodoroPhase.Work));
        Assert.Equal(TimeSpan.FromMinutes(5), Pomodoro.DurationOf(PomodoroPhase.ShortBreak));
        Assert.Equal(TimeSpan.FromMinutes(15), Pomodoro.DurationOf(PomodoroPhase.LongBreak));
        Assert.Equal(TimeSpan.FromMinutes(10), Pomodoro.DurationOf(PomodoroPhase.Nsdr));
    }

    // ---------- NSDR cues + points ----------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(89, 0)]
    [InlineData(90, 1)]
    [InlineData(209, 1)]
    [InlineData(210, 2)]
    [InlineData(330, 3)]
    [InlineData(420, 4)]
    [InlineData(539, 4)]
    [InlineData(540, 5)]
    [InlineData(600, 5)]
    [InlineData(5000, 5)]
    public void Nsdr_cue_lookup_by_elapsed(int seconds, int expectedIndex)
    {
        Assert.Equal(expectedIndex, Nsdr.CueIndexAt(TimeSpan.FromSeconds(seconds)));
        Assert.Same(Nsdr.Cues[expectedIndex], Nsdr.CueAt(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Nsdr_cues_are_ordered_and_start_at_zero()
    {
        Assert.Equal(TimeSpan.Zero, Nsdr.Cues[0].At);
        Assert.True(Nsdr.Cues.Zip(Nsdr.Cues.Skip(1)).All(p => p.First.At < p.Second.At));
        Assert.True(Nsdr.Cues[^1].At < Nsdr.Duration);
    }

    [Fact]
    public void Nsdr_is_worth_three_points_and_counts_on_the_graph()
    {
        Assert.Equal(3, EvidencePoints.For(EvidenceType.NsdrCompleted));
        Assert.Equal(3, EvidencePoints.ForContribution(EvidenceType.NsdrCompleted));
    }

    // ---------- services against a real SQLite db ----------

    private static async Task<string> NewMigratedDbAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ltfi-test-{Guid.NewGuid():N}.db");
        await using var db = new TestDbFactory(path).CreateDbContext();
        await db.Database.MigrateAsync();
        return path;
    }

    private static void Cleanup(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-shm", path + "-wal" })
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Nsdr_records_evidence_only_when_completed()
    {
        var path = await NewMigratedDbAsync();
        try
        {
            var factory = new TestDbFactory(path);
            var time = new FakeTime(new DateTimeOffset(2026, 10, 4, 14, 0, 0, TimeSpan.Zero));
            var nsdr = new NsdrService(factory, time);

            // Stopped early: nothing.
            nsdr.Start();
            time.Advance(TimeSpan.FromMinutes(9));
            Assert.False(await nsdr.CompleteIfDueAsync());
            nsdr.Stop();
            Assert.False(nsdr.IsRunning);
            time.Advance(TimeSpan.FromMinutes(5));
            Assert.False(await nsdr.CompleteIfDueAsync());

            await using (var db = factory.CreateDbContext())
            {
                Assert.Empty(db.Evidence.ToList());
            }

            // Full 10:00: one evidence item, recorded once.
            nsdr.Start();
            time.Advance(TimeSpan.FromMinutes(4));
            Assert.Equal(2, nsdr.GetSnapshot()!.CueIndex);
            time.Advance(TimeSpan.FromMinutes(6));
            Assert.True(nsdr.GetSnapshot()!.IsDue);
            Assert.True(await nsdr.CompleteIfDueAsync());
            Assert.False(await nsdr.CompleteIfDueAsync());
            Assert.False(nsdr.IsRunning);

            await using (var db = factory.CreateDbContext())
            {
                var item = Assert.Single(db.Evidence.ToList());
                Assert.Equal(EvidenceType.NsdrCompleted, item.Type);
            }
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Completed_pomodoros_are_persisted_on_pause_and_finish()
    {
        var path = await NewMigratedDbAsync();
        try
        {
            var factory = new TestDbFactory(path);
            var focus = new FocusSessionService(factory);

            await focus.StartAsync(null, null, "deep work");
            await focus.CompletePomodoroAsync();
            Assert.Equal(FocusSessionStatus.Paused, focus.GetActiveSnapshot()!.Status);
            Assert.Equal(1, focus.GetActiveSnapshot()!.PomodorosCompleted);

            await using (var db = factory.CreateDbContext())
            {
                Assert.Equal(1, db.FocusSessions.Single().PomodorosCompleted);
            }

            await focus.ResumeAsync();
            await focus.CompletePomodoroAsync();
            await focus.FinishAsync(FocusSessionResult.Completed, null, null, null);

            await using (var db = factory.CreateDbContext())
            {
                var session = db.FocusSessions.Single();
                Assert.Equal(2, session.PomodorosCompleted);
                Assert.Equal(FocusSessionStatus.Completed, session.Status);
            }
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Pomodoro_run_pauses_for_breaks_and_tracks_only_work_time()
    {
        var path = await NewMigratedDbAsync();
        try
        {
            var factory = new TestDbFactory(path);
            var time = new FakeTime(new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero));
            var focus = new FocusSessionService(factory, time);
            var nsdr = new NsdrService(factory, time);
            var pomodoro = new PomodoroService(focus, nsdr, time);

            await pomodoro.StartAsync(null, null, "write");
            Assert.Equal(TimeSpan.FromMinutes(25), pomodoro.GetSnapshot()!.Remaining);

            time.Advance(TimeSpan.FromMinutes(24));
            Assert.Equal(PomodoroTransition.None, await pomodoro.AdvanceAsync());
            Assert.Equal(TimeSpan.FromMinutes(1), pomodoro.GetSnapshot()!.Remaining);

            // Work hits 0: counted, session paused, short break running.
            time.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(PomodoroTransition.WorkEnded, await pomodoro.AdvanceAsync());
            var snap = pomodoro.GetSnapshot()!;
            Assert.Equal(PomodoroPhase.ShortBreak, snap.Phase);
            Assert.Equal(1, snap.CompletedWork);
            Assert.Equal(FocusSessionStatus.Paused, focus.GetActiveSnapshot()!.Status);

            // Break time is not work time; break ends and waits for the user.
            time.Advance(TimeSpan.FromMinutes(5));
            Assert.Equal(PomodoroTransition.BreakEnded, await pomodoro.AdvanceAsync());
            Assert.True(pomodoro.GetSnapshot()!.IsBreakOver);
            Assert.Equal(PomodoroTransition.None, await pomodoro.AdvanceAsync());
            Assert.Equal(TimeSpan.FromMinutes(25), focus.GetActiveSnapshot()!.Elapsed);
            Assert.Equal(FocusSessionStatus.Paused, focus.GetActiveSnapshot()!.Status);

            await pomodoro.StartNextAsync();
            Assert.Equal(PomodoroPhase.Work, pomodoro.GetSnapshot()!.Phase);
            Assert.Equal(FocusSessionStatus.Active, focus.GetActiveSnapshot()!.Status);
            time.Advance(TimeSpan.FromMinutes(10));
            Assert.Equal(TimeSpan.FromMinutes(15), pomodoro.GetSnapshot()!.Remaining);

            // Finish mid-interval: only work time (25 + 10) is recorded, with 1 pomodoro.
            await focus.FinishAsync(FocusSessionResult.Partial, null, null, null);
            Assert.False(pomodoro.IsActive);

            await using var db = factory.CreateDbContext();
            var session = db.FocusSessions.Single();
            Assert.Equal(TimeSpan.FromMinutes(35), session.Duration);
            Assert.Equal(1, session.PomodorosCompleted);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Long_break_can_be_swapped_for_nsdr_which_logs_evidence()
    {
        var path = await NewMigratedDbAsync();
        try
        {
            var factory = new TestDbFactory(path);
            var time = new FakeTime(new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero));
            var focus = new FocusSessionService(factory, time);
            var nsdr = new NsdrService(factory, time);
            var pomodoro = new PomodoroService(focus, nsdr, time);

            await pomodoro.StartAsync(null, null, null);
            for (var i = 0; i < 3; i++)
            {
                time.Advance(Pomodoro.WorkDuration);
                Assert.Equal(PomodoroTransition.WorkEnded, await pomodoro.AdvanceAsync());
                Assert.Equal(PomodoroPhase.ShortBreak, pomodoro.GetSnapshot()!.Phase);
                await pomodoro.SkipBreakAsync();
            }

            time.Advance(Pomodoro.WorkDuration);
            Assert.Equal(PomodoroTransition.WorkEnded, await pomodoro.AdvanceAsync());
            var snap = pomodoro.GetSnapshot()!;
            Assert.Equal(PomodoroPhase.LongBreak, snap.Phase);
            Assert.True(snap.CanTakeNsdr);
            Assert.Equal("●●●●", snap.Dots);

            pomodoro.TakeNsdrInstead();
            Assert.Equal(PomodoroPhase.Nsdr, pomodoro.GetSnapshot()!.Phase);
            Assert.True(nsdr.IsRunning);

            time.Advance(Nsdr.Duration);
            Assert.Equal(PomodoroTransition.BreakEnded, await pomodoro.AdvanceAsync());
            Assert.True(pomodoro.GetSnapshot()!.IsBreakOver);

            await pomodoro.StartNextAsync();
            Assert.Equal(PomodoroPhase.Work, pomodoro.GetSnapshot()!.Phase);
            Assert.Equal(4, focus.GetActiveSnapshot()!.PomodorosCompleted);

            await using var db = factory.CreateDbContext();
            var item = Assert.Single(db.Evidence.ToList());
            Assert.Equal(EvidenceType.NsdrCompleted, item.Type);
            Assert.Equal(focus.GetActiveSnapshot()!.Id, item.FocusSessionId);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Skipping_an_nsdr_part_way_records_nothing()
    {
        var path = await NewMigratedDbAsync();
        try
        {
            var factory = new TestDbFactory(path);
            var time = new FakeTime(new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero));
            var focus = new FocusSessionService(factory, time);
            var nsdr = new NsdrService(factory, time);
            var pomodoro = new PomodoroService(focus, nsdr, time);

            await pomodoro.StartAsync(null, null, null);
            for (var i = 0; i < 4; i++)
            {
                time.Advance(Pomodoro.WorkDuration);
                await pomodoro.AdvanceAsync();
                if (i < 3) await pomodoro.SkipBreakAsync();
            }

            pomodoro.TakeNsdrInstead();
            time.Advance(TimeSpan.FromMinutes(6));
            await pomodoro.SkipBreakAsync();
            Assert.False(nsdr.IsRunning);
            time.Advance(TimeSpan.FromMinutes(10));
            await pomodoro.AdvanceAsync();

            await using var db = factory.CreateDbContext();
            Assert.DoesNotContain(db.Evidence.ToList(), e => e.Type == EvidenceType.NsdrCompleted);
        }
        finally { Cleanup(path); }
    }
}
