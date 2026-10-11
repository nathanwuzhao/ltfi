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

/// <summary>
/// NSDR started inside a focus session (pomodoro work interval, a break, FREE mode), and the
/// Command Center's project codes + stable colours.
/// </summary>
public sealed class SessionNsdrAndCodesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ltfi-snsdr-{Guid.NewGuid():N}.db");
    private readonly TestDbFactory _factory;
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero));

    public SessionNsdrAndCodesTests()
    {
        _factory = new TestDbFactory(_path);
        using var db = _factory.CreateDbContext();
        db.Database.Migrate();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-shm", _path + "-wal" })
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }

    private (FocusSessionService Focus, NsdrService Nsdr, PomodoroService Pomodoro) NewServices()
    {
        var focus = new FocusSessionService(_factory, _time);
        var nsdr = new NsdrService(_factory, _time);
        return (focus, nsdr, new PomodoroService(focus, nsdr, _time));
    }

    // ---------------------------------------------------------------- pure state machine

    [Fact]
    public void Nsdr_from_a_work_interval_returns_to_the_same_interval()
    {
        var work = new PomodoroCycle(PomodoroPhase.Work, 2);
        Assert.True(work.CanTakeNsdr);

        var nsdr = work.TakeNsdr();
        Assert.Equal(PomodoroPhase.Nsdr, nsdr.Phase);
        Assert.True(nsdr.NsdrFromWork);
        Assert.Equal(NsdrReturn.ResumeWork, nsdr.AfterNsdr);
        Assert.False(nsdr.CanTakeNsdr);
        Assert.Throws<InvalidOperationException>(() => nsdr.TakeNsdr());
        Assert.Throws<InvalidOperationException>(() => nsdr.CompleteWork());

        var back = nsdr.EndBreak();
        Assert.Equal(new PomodoroCycle(PomodoroPhase.Work, 2), back); // same count, flag cleared
        Assert.Equal("●●○○", nsdr.Dots); // dots unchanged while resting mid-interval

        // After a full cycle, an NSDR in the 5th interval shows the fresh (empty) cycle, not ●●●●.
        Assert.Equal(0, new PomodoroCycle(PomodoroPhase.Work, 4).TakeNsdr().DotsFilled);
    }

    [Fact]
    public void Nsdr_from_any_break_replaces_the_rest_of_it()
    {
        var shortBreak = PomodoroCycle.Start().CompleteWork();
        var nsdr = shortBreak.TakeNsdr();
        Assert.Equal(PomodoroPhase.Nsdr, nsdr.Phase);
        Assert.False(nsdr.NsdrFromWork);
        Assert.Equal(NsdrReturn.StartNextPomodoro, nsdr.AfterNsdr);
        Assert.Equal(1, nsdr.EndBreak().CompletedWork);

        // The long break's TAKE NSDR INSTEAD is the same transition; still long-break only.
        var longBreak = new PomodoroCycle(PomodoroPhase.Work, 3).CompleteWork();
        Assert.Equal(longBreak.TakeNsdr(), longBreak.TakeNsdrInstead());
        Assert.Equal(4, longBreak.TakeNsdr().DotsFilled);
        Assert.Throws<InvalidOperationException>(() => shortBreak.TakeNsdrInstead());
    }

    [Fact]
    public void What_a_session_offers_after_an_nsdr()
    {
        Assert.Equal(NsdrReturn.None, PomodoroCycle.AfterSessionNsdr(false, null));
        Assert.Equal(NsdrReturn.ResumeSession, PomodoroCycle.AfterSessionNsdr(true, null));
        Assert.Equal(NsdrReturn.ResumeWork, PomodoroCycle.AfterSessionNsdr(true, PomodoroCycle.Start().TakeNsdr()));
        Assert.Equal(NsdrReturn.StartNextPomodoro,
            PomodoroCycle.AfterSessionNsdr(true, PomodoroCycle.Start().CompleteWork().TakeNsdr()));
        Assert.Equal(NsdrReturn.None, PomodoroCycle.Start().AfterNsdr);
    }

    // ---------------------------------------------------------------- services, fake clock

    [Fact]
    public async Task Nsdr_during_work_pauses_the_interval_and_preserves_its_remaining_time()
    {
        var (focus, nsdr, pomodoro) = NewServices();
        await pomodoro.StartAsync(null, null, "write");
        _time.Advance(TimeSpan.FromMinutes(10)); // 15:00 left

        Assert.True(await pomodoro.TakeNsdrAsync());
        Assert.False(await pomodoro.TakeNsdrAsync()); // one at a time
        var snap = pomodoro.GetSnapshot()!;
        Assert.Equal(PomodoroPhase.Nsdr, snap.Phase);
        Assert.True(snap.IsNsdrFromWork);
        Assert.True(snap.IsBreak); // the Command Center's button reads END NSDR, not RESUME
        Assert.Equal(FocusSessionStatus.Paused, focus.GetActiveSnapshot()!.Status);
        Assert.Equal(focus.GetActiveSnapshot()!.Id, nsdr.GetSnapshot()!.FocusSessionId);

        _time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(PomodoroTransition.None, await pomodoro.AdvanceAsync());
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(PomodoroTransition.NsdrEnded, await pomodoro.AdvanceAsync());

        // Back to the same work interval, paused, at exactly 15:00; NSDR time isn't work time.
        snap = pomodoro.GetSnapshot()!;
        Assert.Equal(PomodoroPhase.Work, snap.Phase);
        Assert.True(snap.IsWorkPaused);
        Assert.Equal(TimeSpan.FromMinutes(15), snap.Remaining);
        Assert.Equal(TimeSpan.FromMinutes(10), focus.GetActiveSnapshot()!.Elapsed);
        Assert.Equal(0, snap.CompletedWork);
        _time.Advance(TimeSpan.FromMinutes(3)); // still paused: nothing moves
        Assert.Equal(PomodoroTransition.None, await pomodoro.AdvanceAsync());
        Assert.Equal(TimeSpan.FromMinutes(15), pomodoro.GetSnapshot()!.Remaining);

        // RESUME WORK: the interval finishes after the remaining 15:00.
        await focus.ResumeAsync();
        _time.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(PomodoroTransition.WorkEnded, await pomodoro.AdvanceAsync());
        Assert.Equal(1, pomodoro.GetSnapshot()!.CompletedWork);

        // Completed NSDR evidence links the session.
        await using var db = _factory.CreateDbContext();
        var item = Assert.Single(db.Evidence.Where(e => e.Type == EvidenceType.NsdrCompleted).ToList());
        Assert.Equal(focus.GetActiveSnapshot()!.Id, item.FocusSessionId);
    }

    [Fact]
    public async Task Stopping_a_work_interval_nsdr_early_returns_to_paused_work_and_records_nothing()
    {
        var (focus, nsdr, pomodoro) = NewServices();
        await pomodoro.StartAsync(null, null, null);
        _time.Advance(TimeSpan.FromMinutes(20)); // 5:00 left

        Assert.True(await pomodoro.TakeNsdrAsync());
        _time.Advance(TimeSpan.FromMinutes(4));
        await pomodoro.SkipBreakAsync(); // STOP / the Command Center's END NSDR

        Assert.False(nsdr.IsRunning);
        var snap = pomodoro.GetSnapshot()!;
        Assert.Equal(PomodoroPhase.Work, snap.Phase);
        Assert.True(snap.IsWorkPaused); // not auto-resumed
        Assert.Equal(TimeSpan.FromMinutes(5), snap.Remaining);

        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(PomodoroTransition.None, await pomodoro.AdvanceAsync());
        await using var db = _factory.CreateDbContext();
        Assert.DoesNotContain(db.Evidence.ToList(), e => e.Type == EvidenceType.NsdrCompleted);
    }

    [Fact]
    public async Task Nsdr_during_a_short_break_ends_the_break_and_offers_the_next_pomodoro()
    {
        var (focus, _, pomodoro) = NewServices();
        await pomodoro.StartAsync(null, null, null);
        _time.Advance(Pomodoro.WorkDuration);
        Assert.Equal(PomodoroTransition.WorkEnded, await pomodoro.AdvanceAsync());
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.True(await pomodoro.TakeNsdrAsync());
        var snap = pomodoro.GetSnapshot()!;
        Assert.Equal(PomodoroPhase.Nsdr, snap.Phase);
        Assert.False(snap.IsNsdrFromWork);

        // Past the original 5:00 break: the NSDR, not the break, is running.
        _time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(PomodoroTransition.None, await pomodoro.AdvanceAsync());
        _time.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(PomodoroTransition.BreakEnded, await pomodoro.AdvanceAsync());
        Assert.True(pomodoro.GetSnapshot()!.IsBreakOver);
        Assert.Equal(FocusSessionStatus.Paused, focus.GetActiveSnapshot()!.Status);

        await pomodoro.StartNextAsync();
        Assert.Equal(PomodoroPhase.Work, pomodoro.GetSnapshot()!.Phase);
        Assert.Equal(Pomodoro.WorkDuration, focus.GetActiveSnapshot()!.Elapsed); // only work time

        await using var db = _factory.CreateDbContext();
        var item = Assert.Single(db.Evidence.Where(e => e.Type == EvidenceType.NsdrCompleted).ToList());
        Assert.Equal(focus.GetActiveSnapshot()!.Id, item.FocusSessionId);
    }

    [Fact]
    public async Task Nsdr_in_a_free_session_pauses_it_and_leaves_it_paused_for_resume()
    {
        var (focus, nsdr, pomodoro) = NewServices();
        await focus.StartAsync(null, null, "read");
        _time.Advance(TimeSpan.FromMinutes(30));

        Assert.False(pomodoro.IsActive);
        Assert.True(await pomodoro.TakeNsdrAsync());
        var session = focus.GetActiveSnapshot()!;
        Assert.Equal(FocusSessionStatus.Paused, session.Status);
        Assert.Equal(session.Id, nsdr.GetSnapshot()!.FocusSessionId);

        _time.Advance(Nsdr.Duration);
        Assert.True(await nsdr.CompleteIfDueAsync());
        Assert.False(nsdr.IsRunning);

        // Still paused; NSDR time isn't session time; RESUME continues it.
        Assert.Equal(FocusSessionStatus.Paused, focus.GetActiveSnapshot()!.Status);
        Assert.Equal(TimeSpan.FromMinutes(30), focus.GetActiveSnapshot()!.Elapsed);
        await focus.ResumeAsync();
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.FromMinutes(35), focus.GetActiveSnapshot()!.Elapsed);

        await using var db = _factory.CreateDbContext();
        var item = Assert.Single(db.Evidence.Where(e => e.Type == EvidenceType.NsdrCompleted).ToList());
        Assert.Equal(session.Id, item.FocusSessionId);
        Assert.Equal(3, EvidencePoints.For(item.Type));
    }

    [Fact]
    public async Task No_session_means_no_session_nsdr()
    {
        var (_, nsdr, pomodoro) = NewServices();
        Assert.False(await pomodoro.TakeNsdrAsync());
        Assert.False(nsdr.IsRunning);
    }

    // ---------------------------------------------------------------- project codes + colours

    [Theory]
    [InlineData("boids 01", "BOID")]
    [InlineData("mirolab research", "MIRO")]
    [InlineData("Life", "LIFE")]
    [InlineData("AI", "AI")]
    [InlineData("a-b c.d e", "ABCD")]
    [InlineData("  x2 ", "X2")]
    [InlineData("—!", "—")]
    [InlineData("", "—")]
    [InlineData(null, "—")]
    public void Project_code_is_the_first_four_letters_or_digits_uppercased(string? title, string expected) =>
        Assert.Equal(expected, ProjectCodes.Code(title));

    [Fact]
    public void Project_colours_are_stable_muted_and_standing_is_neutral()
    {
        var id = Guid.Parse("3f2c9a5e-7b41-4d8e-9c0a-1b2c3d4e5f60");
        // Fixed hash of the id: the same colour on every run (pinned so a change is noticed).
        Assert.Equal(ProjectCodes.ColorFor(id), ProjectCodes.ColorFor(Guid.Parse(id.ToString())));
        Assert.Equal(ProjectCodes.PaletteIndex(id), ProjectCodes.PaletteIndex(id));
        Assert.Contains(ProjectCodes.ColorFor(id), ProjectCodes.Palette);
        Assert.Equal(ProjectCodes.StandingColor, ProjectCodes.ColorFor(id, isStanding: true));
        Assert.DoesNotContain(ProjectCodes.StandingColor, ProjectCodes.Palette);

        // 6–8 distinct colours, none of them the status green/amber/red.
        Assert.InRange(ProjectCodes.Palette.Count, 6, 8);
        Assert.Equal(ProjectCodes.Palette.Count, ProjectCodes.Palette.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain("#46D17F", ProjectCodes.Palette);
        Assert.DoesNotContain("#E6A13A", ProjectCodes.Palette);
        Assert.DoesNotContain("#E5484D", ProjectCodes.Palette);
        Assert.All(ProjectCodes.Palette, hex => Assert.Matches("^#[0-9A-F]{6}$", hex));

        // Readable on the near-black background: every colour is reasonably light.
        Assert.All(ProjectCodes.Palette, hex =>
        {
            var r = Convert.ToInt32(hex[1..3], 16);
            var g = Convert.ToInt32(hex[3..5], 16);
            var b = Convert.ToInt32(hex[5..7], 16);
            Assert.True(0.2126 * r + 0.7152 * g + 0.0722 * b > 110, hex);
        });

        // Different projects spread over the palette (not all one colour).
        var used = Enumerable.Range(0, 64)
            .Select(i => ProjectCodes.PaletteIndex(new Guid(i, 7, 7, 1, 2, 3, 4, 5, 6, 7, 8)))
            .Distinct()
            .Count();
        Assert.True(used >= ProjectCodes.Palette.Count - 1);
    }

    [Fact]
    public void Project_colour_index_is_pinned_for_a_known_id()
    {
        // FNV-1a over Guid.ToByteArray() — a stable function, not string.GetHashCode (randomised per run).
        var id = Guid.Parse("00000000-0000-0000-0000-000000000000");
        uint hash = 2166136261;
        for (var i = 0; i < 16; i++) hash = (hash ^ 0) * 16777619;
        Assert.Equal((int)(hash % (uint)ProjectCodes.Palette.Count), ProjectCodes.PaletteIndex(id));
    }
}
