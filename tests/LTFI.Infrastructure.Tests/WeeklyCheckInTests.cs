using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using LTFI.Core.Domain;
using LTFI.Infrastructure.Services;
using LTFI.Infrastructure.Settings;
using Xunit;

namespace LTFI.Infrastructure.Tests;

/// <summary>
/// Weekly check-in: the pure schedule (Mon–Sun week, Sat 00:00 open, Sun 18:00 gate, Sun 23:59 due,
/// Mon 00:00 overdue), late mapping, snoozes per reviewed week, the owner's real history, settings,
/// and the ReflectionService round trip. Every timestamp carries an explicit America/New_York-like
/// offset (EDT, -04:00) and the service tests use a fixed -04:00 zone, so nothing depends on the
/// machine's time zone.
/// </summary>
public class WeeklyCheckInTests
{
    private static readonly TimeSpan Edt = TimeSpan.FromHours(-4);
    private static readonly CheckInSchedule S = CheckInSchedule.Default;

    private static readonly DateOnly Sep28 = new(2026, 9, 28);
    private static readonly DateOnly Oct5 = new(2026, 10, 5);
    private static readonly DateOnly Oct12 = new(2026, 10, 12);
    private static readonly DateOnly Oct19 = new(2026, 10, 19);
    private static readonly DateOnly Oct26 = new(2026, 10, 26);

    /// <summary>The owner's two real check-ins (from the live DB): Sun Oct 4 18:40 and Sat Oct 10 12:54 EDT.</summary>
    private static readonly DateTimeOffset OwnerFirst = new DateTimeOffset(2026, 10, 4, 18, 40, 19, Edt).AddTicks(5977007);
    private static readonly DateTimeOffset OwnerSecond = new DateTimeOffset(2026, 10, 10, 12, 54, 0, Edt).AddTicks(2547152);

    private static DateTimeOffset At(int month, int day, int hour, int minute, int second = 0) =>
        new(2026, month, day, hour, minute, second, Edt);

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
    public void Weeks_run_monday_to_sunday_and_the_default_schedule_is_sat_sun()
    {
        Assert.Equal(Oct5, WeeklyCheckIn.MondayOf(At(10, 5, 0, 0)));
        Assert.Equal(Oct5, WeeklyCheckIn.MondayOf(At(10, 11, 23, 59, 59)));
        Assert.Equal(Oct12, WeeklyCheckIn.MondayOf(At(10, 12, 0, 0)));

        Assert.Equal(TimeSpan.FromDays(5), S.Opens);                                   // Sat 00:00
        Assert.Equal(TimeSpan.FromDays(6) + TimeSpan.FromHours(18), S.GateFrom);        // Sun 18:00
        Assert.Equal(TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1), S.Due);            // Sun 23:59
        Assert.Equal(TimeSpan.FromDays(7), S.Deadline);                                 // Mon 00:00
        Assert.Equal(2, S.MaxSnoozes);
        Assert.Equal(3, S.SnoozeHours);
    }

    [Fact]
    public void A_check_in_reviews_this_week_once_the_window_opens_otherwise_last_week_late()
    {
        Assert.Equal(Sep28, WeeklyCheckIn.ReviewedWeekOf(At(10, 9, 23, 59, 59), S));    // Fri: before the window → last week
        Assert.True(WeeklyCheckIn.IsLate(At(10, 9, 23, 59, 59), S));
        Assert.Equal(Oct5, WeeklyCheckIn.ReviewedWeekOf(At(10, 10, 0, 0), S));          // Sat 00:00: window open
        Assert.False(WeeklyCheckIn.IsLate(At(10, 10, 0, 0), S));
        Assert.Equal(Oct5, WeeklyCheckIn.ReviewedWeekOf(At(10, 11, 23, 59, 59), S));    // Sun 23:59:59: still on time
        Assert.False(WeeklyCheckIn.IsLate(At(10, 11, 23, 59, 59), S));
        Assert.Equal(Oct5, WeeklyCheckIn.ReviewedWeekOf(At(10, 12, 0, 0), S));          // Mon 00:00: late for Oct 5
        Assert.True(WeeklyCheckIn.IsLate(At(10, 12, 0, 0), S));
        Assert.Equal(Oct5, WeeklyCheckIn.ReviewedWeekOf(At(10, 16, 23, 59, 59), S));    // Fri: still late for Oct 5
        Assert.Equal(Oct12, WeeklyCheckIn.ReviewedWeekOf(At(10, 17, 0, 0), S));         // next window

        // Commitments made in the check-in for week W apply to W+1.
        Assert.Equal(Oct12, WeeklyCheckIn.CommitmentWeekFor(Oct5));
        Assert.Equal(Oct12, WeeklyCommitments.WeekFor(At(10, 10, 9, 0), S));
        Assert.Equal(Oct12, WeeklyCommitments.WeekFor(At(10, 14, 9, 0), S));            // a late one (Wed) too
        Assert.True(WeeklyCommitments.IsLegacyWeekStart(new DateOnly(2026, 10, 4)));    // old Sunday-start value
        Assert.False(WeeklyCommitments.IsLegacyWeekStart(Oct12));
    }

    [Fact]
    public void Owner_history_maps_on_time_and_nothing_is_due_on_sunday_oct_11()
    {
        Assert.Equal(Sep28, WeeklyCheckIn.ReviewedWeekOf(OwnerFirst, S));   // Sun Oct 4 → week Sep 28 – Oct 4
        Assert.False(WeeklyCheckIn.IsLate(OwnerFirst, S));
        Assert.Equal(Oct5, WeeklyCheckIn.ReviewedWeekOf(OwnerSecond, S));   // Sat Oct 10 → week Oct 5 – 11
        Assert.False(WeeklyCheckIn.IsLate(OwnerSecond, S));

        // Before the Saturday check-in, Saturday morning was "open" (form, chip) but not gated.
        var satMorning = WeeklyCheckIn.Evaluate(OwnerFirst, At(10, 10, 9, 0), S);
        Assert.Equal(CheckInPhase.Open, satMorning.Phase);
        Assert.Equal(Oct5, satMorning.ReviewWeek);
        Assert.False(WeeklyCheckIn.MustShow(OwnerFirst, null, At(10, 10, 9, 0), S));

        // All of Sunday Oct 11 — including after the 18:00 gate time — nothing is due.
        for (var t = At(10, 11, 0, 0); t <= At(10, 11, 23, 59, 59); t = t.AddMinutes(30))
        {
            var s = WeeklyCheckIn.Evaluate(OwnerSecond, t, S);
            Assert.Equal(CheckInPhase.Done, s.Phase);
            Assert.False(s.IsGatePhase);
            Assert.True(s.CanSubmit);                                           // can still revise until 23:59
            Assert.True(s.IsRevision);
            Assert.False(WeeklyCheckIn.MustShow(OwnerSecond, null, t, S));
            Assert.Equal(At(10, 17, 0, 0), s.NextOpensAt);
        }

        // Monday: done, and the form is closed until SAT OCT 17.
        var monday = WeeklyCheckIn.Evaluate(OwnerSecond, At(10, 12, 9, 0), S);
        Assert.Equal(CheckInPhase.Done, monday.Phase);
        Assert.False(monday.CanSubmit);
        Assert.Equal(Oct5, monday.ReviewWeek);
        Assert.Equal(At(10, 17, 0, 0), monday.NextOpensAt);
    }

    [Fact]
    public void Window_boundaries_open_gate_due_and_overdue()
    {
        var last = OwnerSecond; // week Oct 5 reviewed; next up is week Oct 12

        CheckInState Eval(DateTimeOffset now) => WeeklyCheckIn.Evaluate(last, now, S);

        var fri = Eval(At(10, 16, 23, 59));
        Assert.Equal(CheckInPhase.Done, fri.Phase);                         // Fri 23:59: not open
        Assert.False(fri.CanSubmit);

        var sat = Eval(At(10, 17, 0, 0));
        Assert.Equal(CheckInPhase.Open, sat.Phase);                         // Sat 00:00: open, no gate
        Assert.Equal(Oct12, sat.ReviewWeek);
        Assert.True(sat.CanSubmit);
        Assert.False(sat.IsGatePhase);
        Assert.Equal(At(10, 17, 0, 0), sat.OpensAt);
        Assert.Equal(At(10, 18, 18, 0), sat.GateAt);
        Assert.Equal(At(10, 18, 23, 59), sat.DueAt);
        Assert.Equal(At(10, 24, 0, 0), sat.NextOpensAt);

        Assert.Equal(CheckInPhase.Open, Eval(At(10, 18, 17, 59, 59)).Phase);
        Assert.False(WeeklyCheckIn.MustShow(last, null, At(10, 18, 17, 59, 59), S));

        Assert.Equal(CheckInPhase.Closing, Eval(At(10, 18, 18, 0)).Phase);  // Sun 18:00: gate
        Assert.True(WeeklyCheckIn.MustShow(last, null, At(10, 18, 18, 0), S));

        Assert.Equal(CheckInPhase.Closing, Eval(At(10, 18, 23, 59, 59)).Phase); // Sun 23:59:59: due, not overdue

        var mon = Eval(At(10, 19, 0, 0));
        Assert.Equal(CheckInPhase.Overdue, mon.Phase);                      // Mon 00:00: overdue
        Assert.Equal(Oct12, mon.ReviewWeek);
        Assert.True(mon.CanSubmit);
        Assert.True(WeeklyCheckIn.MustShow(last, null, At(10, 19, 0, 0), S));

        Assert.Equal(CheckInPhase.Overdue, Eval(At(10, 23, 23, 59, 59)).Phase); // all week until the next window
        var nextSat = Eval(At(10, 24, 0, 0));
        Assert.Equal(CheckInPhase.Open, nextSat.Phase);                     // Oct 12 is abandoned; Oct 19 opens
        Assert.Equal(Oct19, nextSat.ReviewWeek);

        // A late check-in (Mon Oct 19 09:00) reviews Oct 12 and clears it; Mon–Fri stays closed.
        var late = At(10, 19, 9, 0);
        Assert.Equal(Oct12, WeeklyCheckIn.ReviewedWeekOf(late, S));
        Assert.Equal(CheckInPhase.Done, WeeklyCheckIn.Evaluate(late, At(10, 19, 9, 1), S).Phase);
        Assert.False(WeeklyCheckIn.Evaluate(late, At(10, 23, 12, 0), S).CanSubmit);
        Assert.Equal(CheckInPhase.Open, WeeklyCheckIn.Evaluate(late, At(10, 24, 0, 0), S).Phase);
    }

    [Fact]
    public void A_fresh_install_is_overdue_midweek_and_open_on_saturday()
    {
        var wed = WeeklyCheckIn.Evaluate(null, At(10, 14, 12, 0), S);
        Assert.Equal(CheckInPhase.Overdue, wed.Phase);
        Assert.Equal(Oct5, wed.ReviewWeek);
        Assert.Equal(CheckInPhase.Open, WeeklyCheckIn.Evaluate(null, At(10, 17, 12, 0), S).Phase);
    }

    [Fact]
    public void Snoozes_are_capped_per_reviewed_week_and_only_while_gated()
    {
        var last = OwnerSecond;

        // Nothing to snooze while done or merely open.
        Assert.Throws<InvalidOperationException>(() => WeeklyCheckIn.Snooze(null, last, At(10, 14, 12, 0), S));
        Assert.Throws<InvalidOperationException>(() => WeeklyCheckIn.Snooze(null, last, At(10, 17, 10, 0), S));

        var s1 = WeeklyCheckIn.Snooze(null, last, At(10, 18, 18, 30), S);
        Assert.Equal(1, s1.Count);
        Assert.Equal(Oct12, DateOnly.FromDateTime(s1.WeekStart.DateTime));
        Assert.Equal(At(10, 18, 21, 30), s1.Until);
        Assert.True(WeeklyCheckIn.IsSnoozed(s1, At(10, 18, 19, 0), S));
        Assert.False(WeeklyCheckIn.MustShow(last, s1, At(10, 18, 19, 0), S));
        Assert.True(WeeklyCheckIn.MustShow(last, s1, At(10, 18, 21, 30), S));   // snooze expired

        // Overdue Monday is the same reviewed week, so the cap carries over.
        var s2 = WeeklyCheckIn.Snooze(s1, last, At(10, 19, 1, 0), S);
        Assert.Equal(0, WeeklyCheckIn.SnoozesRemaining(s2, At(10, 19, 1, 0), S));
        Assert.False(WeeklyCheckIn.MustShow(last, s2, At(10, 19, 3, 0), S));
        Assert.True(WeeklyCheckIn.MustShow(last, s2, At(10, 19, 4, 0), S));
        Assert.Throws<InvalidOperationException>(() => WeeklyCheckIn.Snooze(s2, last, At(10, 19, 5, 0), S));

        // The next review week starts over, and the old snooze no longer applies.
        var nextGate = At(10, 25, 18, 0);
        Assert.Equal(S.MaxSnoozes, WeeklyCheckIn.SnoozesRemaining(s2, nextGate, S));
        Assert.False(WeeklyCheckIn.IsSnoozed(s2, nextGate, S));

        // State written by the old Sunday-week model (the owner's file) counts as none used.
        var legacy = new CheckInSnoozeState(new DateTimeOffset(2026, 10, 11, 0, 0, 0, Edt), 1, new DateTimeOffset(2026, 10, 11, 3, 8, 20, Edt));
        Assert.Equal(S.MaxSnoozes, WeeklyCheckIn.SnoozesRemaining(legacy, At(10, 18, 18, 0), S));
        Assert.Equal(S.MaxSnoozes, WeeklyCheckIn.SnoozesRemaining(legacy, At(10, 11, 18, 0), S));
    }

    [Fact]
    public void Schedule_comes_from_settings_and_bad_values_fall_back_to_defaults()
    {
        Assert.Equal(S, new CheckInSettings().ToSchedule());

        var custom = new CheckInSettings
        {
            OpensDay = "friday", OpensTime = "17:00",
            GateFromDay = "Sunday", GateFromTime = "12:00",
            DueDay = "Sunday", DueTime = "20:00",
            MaxSnoozesPerWeek = 1, SnoozeHours = 2
        }.ToSchedule();
        Assert.Equal(CheckInSchedule.At(DayOfWeek.Friday, 17, 0), custom.Opens);
        Assert.Equal(1, custom.MaxSnoozes);
        Assert.Equal(Oct5, WeeklyCheckIn.ReviewedWeekOf(At(10, 16, 16, 59), custom));
        Assert.Equal(Oct12, WeeklyCheckIn.ReviewedWeekOf(At(10, 16, 17, 0), custom));
        Assert.Equal(CheckInPhase.Open, WeeklyCheckIn.Evaluate(OwnerSecond, At(10, 18, 11, 59), custom).Phase);
        Assert.Equal(CheckInPhase.Closing, WeeklyCheckIn.Evaluate(OwnerSecond, At(10, 18, 20, 0, 59), custom).Phase);
        Assert.Equal(CheckInPhase.Overdue, WeeklyCheckIn.Evaluate(OwnerSecond, At(10, 18, 20, 1), custom).Phase);

        Assert.Same(CheckInSchedule.Default, new CheckInSettings { OpensDay = "Funday" }.ToSchedule());
        Assert.Same(CheckInSchedule.Default, new CheckInSettings { OpensDay = "5" }.ToSchedule());
        Assert.Same(CheckInSchedule.Default, new CheckInSettings { DueTime = "25:00" }.ToSchedule());
        Assert.Same(CheckInSchedule.Default, new CheckInSettings { GateFromDay = "Friday" }.ToSchedule()); // gate before open
        Assert.Same(CheckInSchedule.Default, new CheckInSettings { SnoozeHours = 0 }.ToSchedule());
    }

    [Fact]
    public void Settings_file_without_a_check_in_section_gets_one_written_with_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ltfi-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "reminders": { "standingProject": "Life" } }""");
            var settings = SettingsStore.Load(path);
            Assert.Equal("Saturday", settings.CheckIn.OpensDay);

            var text = File.ReadAllText(path);
            Assert.Contains("\"checkIn\"", text);
            Assert.Contains("\"gateFromTime\": \"18:00\"", text);
            Assert.Contains("\"dueTime\": \"23:59\"", text);

            // An edited value is read back.
            File.WriteAllText(path, text.Replace("\"gateFromTime\": \"18:00\"", "\"gateFromTime\": \"20:30\""));
            Assert.Equal(CheckInSchedule.At(DayOfWeek.Sunday, 20, 30), SettingsStore.Load(path).CheckIn.ToSchedule().GateFrom);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
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
    public async Task Service_gates_from_sunday_evening_and_counts_one_check_in_per_reviewed_week()
    {
        using var env = new Env();
        var clock = env.Clock;
        var service = env.Reflections();

        // Saturday: open — form and chip, no gate, nothing to snooze.
        clock.Now = At(10, 17, 10, 0);
        var sat = await service.GetWeeklyCheckInStatusAsync();
        Assert.Equal(CheckInPhase.Open, sat.Phase);
        Assert.True(sat.IsDue);
        Assert.True(sat.CanSubmit);
        Assert.False(sat.MustShow);
        Assert.False(sat.CanSnooze);
        Assert.Equal(Oct12, sat.ReviewWeek);
        Assert.Equal(At(10, 18, 23, 59), sat.DueAt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SnoozeWeeklyCheckInAsync());

        // Sunday 18:00: gated. A snooze persists through the JSON store (a fresh service sees it).
        clock.Now = At(10, 18, 18, 0);
        var gate = await service.GetWeeklyCheckInStatusAsync();
        Assert.True(gate.MustShow);
        Assert.Equal(2, gate.SnoozesRemaining);
        await service.SnoozeWeeklyCheckInAsync();
        var snoozed = await env.Reflections().GetWeeklyCheckInStatusAsync();
        Assert.True(snoozed.IsSnoozed);
        Assert.False(snoozed.MustShow);
        Assert.Equal(1, snoozed.SnoozesRemaining);

        clock.Now = At(10, 18, 18, 5);
        await service.SaveWeeklyCheckInAsync(Answers());
        clock.Now = At(10, 18, 18, 10);
        await service.SaveWeeklyCheckInAsync(Answers("second pass"));          // a revision: saved, no points

        var after = await service.GetWeeklyCheckInStatusAsync();
        Assert.Equal(CheckInPhase.Done, after.Phase);
        Assert.False(after.MustShow);
        Assert.False(after.CanSnooze);
        Assert.True(after.IsRevision);

        var history = await service.GetWeeklyCheckInHistoryAsync();
        Assert.Equal(2, history.Count);
        Assert.Equal("second pass", history[0].Answers[WeeklyCheckIn.CommitmentsQuestionIndex].Answer);
        Assert.All(history, h => Assert.Equal(Oct12, h.ReviewWeek));
        Assert.All(history, h => Assert.False(h.IsLate));
        Assert.Equal(history[0].Id, (await service.GetLatestWeeklyCheckInAsync())!.Id);
        Assert.Single(await env.ReflectionEvidenceAsync());

        // Monday: done, form closed.
        clock.Now = At(10, 19, 9, 0);
        var monday = await service.GetWeeklyCheckInStatusAsync();
        Assert.Equal(CheckInPhase.Done, monday.Phase);
        Assert.False(monday.CanSubmit);
        Assert.Equal(At(10, 24, 0, 0), monday.NextOpensAt);

        // Skip the Oct 19 window: overdue from Mon Oct 26, gated at once with a fresh snooze budget.
        clock.Now = At(10, 26, 9, 0);
        var overdue = await service.GetWeeklyCheckInStatusAsync();
        Assert.Equal(CheckInPhase.Overdue, overdue.Phase);
        Assert.Equal(Oct19, overdue.ReviewWeek);
        Assert.True(overdue.MustShow);
        Assert.Equal(2, overdue.SnoozesRemaining);

        // The late check-in belongs to Oct 19 and earns that week's points.
        var lateRecord = await service.SaveWeeklyCheckInAsync(Answers("late one"));
        Assert.Equal(Oct19, lateRecord.ReviewWeek);
        Assert.True(lateRecord.IsLate);
        Assert.Equal(2, (await env.ReflectionEvidenceAsync()).Count);
        var afterLate = await service.GetWeeklyCheckInStatusAsync();
        Assert.Equal(CheckInPhase.Done, afterLate.Phase);
        Assert.False(afterLate.CanSubmit);

        clock.Now = At(10, 31, 0, 0);
        var next = await service.GetWeeklyCheckInStatusAsync();
        Assert.Equal(CheckInPhase.Open, next.Phase);
        Assert.Equal(Oct26, next.ReviewWeek);
    }

    [Fact]
    public async Task Owner_history_on_sunday_oct_11_is_done_and_ungated_even_after_six_pm()
    {
        using var env = new Env();
        await using (var db = env.Factory.CreateDbContext())
        {
            db.Reflections.Add(Entry(OwnerFirst));
            db.Reflections.Add(Entry(OwnerSecond));
            await db.SaveChangesAsync();
        }

        // The owner's snooze file from the old model (week "2026-10-11", 1 used).
        File.WriteAllText(env.SnoozePath,
            """{"WeekStart":"2026-10-11T00:00:00-04:00","Count":1,"Until":"2026-10-11T03:08:20.1160806-04:00"}""");

        foreach (var now in new[] { At(10, 11, 10, 0), At(10, 11, 18, 0), At(10, 11, 20, 30), At(10, 11, 23, 59, 59) })
        {
            env.Clock.Now = now;
            var status = await env.Reflections().GetWeeklyCheckInStatusAsync();
            Assert.Equal(CheckInPhase.Done, status.Phase);
            Assert.False(status.IsDue);
            Assert.False(status.MustShow);
            Assert.Equal(Oct5, status.ReviewWeek);
            Assert.Equal(At(10, 17, 0, 0), status.NextOpensAt);
        }

        var history = await env.Reflections().GetWeeklyCheckInHistoryAsync();
        Assert.Equal([Oct5, Sep28], history.Select(h => h.ReviewWeek));
        Assert.All(history, h => Assert.False(h.IsLate));
    }

    [Fact]
    public async Task Stored_utc_times_are_mapped_on_the_local_wall_clock()
    {
        // Fri Oct 9 21:00 EDT is Sat Oct 10 01:00 UTC. Stored as UTC it must still count as a
        // Friday (late for Sep 28), not as a Saturday check-in for Oct 5.
        using var env = new Env();
        await using (var db = env.Factory.CreateDbContext())
        {
            db.Reflections.Add(Entry(At(10, 9, 21, 0).ToUniversalTime()));
            await db.SaveChangesAsync();
        }

        env.Clock.Now = At(10, 10, 9, 0);
        var record = Assert.Single(await env.Reflections().GetWeeklyCheckInHistoryAsync());
        Assert.Equal(Sep28, record.ReviewWeek);
        Assert.True(record.IsLate);
        Assert.Equal(CheckInPhase.Open, (await env.Reflections().GetWeeklyCheckInStatusAsync()).Phase);
    }

    private static ReflectionEntry Entry(DateTimeOffset at) => new()
    {
        ScopeType = ReflectionScope.Week,
        Prompt = WeeklyCheckIn.PromptVersion,
        Body = WeeklyCheckIn.Serialize(WeeklyCheckIn.BuildAnswers(Answers())),
        CreatedAt = at
    };

    /// <summary>A migrated temp DB, a snooze file and an EDT-pinned clock.</summary>
    private sealed class Env : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), $"ltfi-ci-{Guid.NewGuid():N}");

        public Env()
        {
            Directory.CreateDirectory(_folder);
            Factory = new TestDbFactory(Path.Combine(_folder, "test.db"));
            using var db = Factory.CreateDbContext();
            db.Database.Migrate();
        }

        public TestDbFactory Factory { get; }

        public FixedZoneClock Clock { get; } = new(At(10, 11, 12, 0), Edt);

        public string SnoozePath => Path.Combine(_folder, "snooze.json");

        public ReflectionService Reflections() => new(Factory, new JsonCheckInSnoozeStore(SnoozePath), Clock);

        public async Task<System.Collections.Generic.List<EvidenceItem>> ReflectionEvidenceAsync()
        {
            await using var db = Factory.CreateDbContext();
            return await db.Evidence.Where(e => e.Type == EvidenceType.ReflectionSubmitted).ToListAsync();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
        }
    }
}

/// <summary>A settable clock in a fixed-offset zone, so local-time week math never depends on the machine.</summary>
internal sealed class FixedZoneClock(DateTimeOffset now, TimeSpan offset) : TimeProvider
{
    private readonly TimeZoneInfo _zone =
        TimeZoneInfo.CreateCustomTimeZone($"LTFI-Test{offset.TotalHours:+0;-0}", offset, "LTFI test zone", "LTFI test zone");

    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

    public override TimeZoneInfo LocalTimeZone => _zone;
}
