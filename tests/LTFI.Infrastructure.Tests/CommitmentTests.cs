using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using LTFI.Infrastructure.Reminders;
using LTFI.Infrastructure.Services;
using LTFI.Infrastructure.Settings;
using Xunit;
using TaskStatus = LTFI.Core.Domain.TaskStatus;

namespace LTFI.Infrastructure.Tests;

/// <summary>
/// Weekly commitments (check-in Q5 made real), the contribution graph's single-day evidence query,
/// and the "standing projects have no progress" rule. Real SQLite in a temp folder.
/// </summary>
public sealed class CommitmentTests : IDisposable
{
    // Saturday 2026-10-10 14:00 (UTC-pinned clock): inside week Oct 5–11's check-in window, so a
    // check-in now reviews Mon Oct 5 – Sun Oct 11 and its commitments apply to Mon Oct 12 – Sun Oct 18.
    private static readonly DateTimeOffset Saturday = new(2026, 10, 10, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly ThisWeek = new(2026, 10, 5);
    private static readonly DateOnly NextWeek = new(2026, 10, 12);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"ltfi-cm-{Guid.NewGuid():N}");
    private readonly TestDbFactory _factory;
    private readonly Clock _clock = new(Saturday);

    public CommitmentTests()
    {
        Directory.CreateDirectory(_folder);
        _factory = new TestDbFactory(Path.Combine(_folder, "test.db"));
        using var db = _factory.CreateDbContext();
        db.Database.Migrate();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    private string SnapshotPath => Path.Combine(_folder, "reminders.json");
    private ReminderOutbox NewOutbox() => new(_factory, () => Path.Combine(_folder, "outbox.json"));
    private ReflectionService NewReflections() =>
        new(_factory, new JsonCheckInSnoozeStore(Path.Combine(_folder, "snooze.json")), _clock);
    private CommitmentService NewCommitments() => new(_factory, _clock);

    /// <summary>The Command Center panel's lines (next week's, once this week's check-in is in).</summary>
    private static async Task<IReadOnlyList<CommitmentLine>> PanelAsync(CommitmentService commitments) =>
        (await commitments.GetPanelAsync()).Lines;

    private void WriteExport(string remindersJson)
    {
        File.WriteAllText(SnapshotPath, $$"""{"schema":"ltfi.reminders/v1","reminders":[{{remindersJson}}]}""");
        File.SetLastWriteTimeUtc(SnapshotPath, DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 100000)));
    }

    private static string?[] Answers() => ["shipped", "", "keep", "energy", "(replaced)", "earlier"];

    private async Task<List<EvidenceItem>> KeptEvidenceAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.Evidence.Where(e => e.Type == EvidenceType.CommitmentKept).ToListAsync();
    }

    // ---------------------------------------------------------------- pure rules

    [Fact]
    public void Legacy_q5_answers_split_into_at_most_three_clean_commitments()
    {
        Assert.Equal(["Ship check-in", "Run 3x", "Call mom"],
            WeeklyCommitments.SplitLegacyAnswer("1. Ship check-in\r\n2) Run 3x\n\n- Call mom\n• Fourth one"));
        Assert.Equal(["foo", "bar baz", "qux"], WeeklyCommitments.SplitLegacyAnswer("1. foo 2. bar baz 3. qux"));
        Assert.Equal(["3-day plan", "1.5 hours of reading"], WeeklyCommitments.SplitLegacyAnswer("3-day plan\n1.5 hours of reading"));
        Assert.Equal(["just one thing"], WeeklyCommitments.SplitLegacyAnswer("  just one thing  "));
        Assert.Empty(WeeklyCommitments.SplitLegacyAnswer("   "));
        Assert.Empty(WeeklyCommitments.SplitLegacyAnswer(null));

        Assert.Equal("1. a\n2. b", WeeklyCommitments.JoinForAnswer(["a", "b"]));
        Assert.Equal(5, EvidencePoints.For(EvidenceType.CommitmentKept));
        Assert.Equal(5, EvidencePoints.ForContribution(EvidenceType.CommitmentKept));
        Assert.Equal(NextWeek, WeeklyCommitments.WeekFor(Saturday, CheckInSchedule.Default));
    }

    // ---------------------------------------------------------------- creation

    [Fact]
    public async Task Check_in_creates_structured_commitments_and_stores_q5_as_joined_text()
    {
        var reflections = NewReflections();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reflections.SaveWeeklyCheckInAsync(Answers(), [new CommitmentDraft("  "), new CommitmentDraft("")]));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reflections.SaveWeeklyCheckInAsync(Answers(), Enumerable.Range(0, 4).Select(i => new CommitmentDraft($"c{i}")).ToList()));

        var record = await reflections.SaveWeeklyCheckInAsync(Answers(),
            [new CommitmentDraft(" Ship it "), new CommitmentDraft(""), new CommitmentDraft("Run", LinkedTaskId: Guid.NewGuid())]);

        Assert.Equal("1. Ship it\n2. Run", record.Answers[WeeklyCheckIn.CommitmentsQuestionIndex].Answer);

        // They apply to next week: the panel switches to them (this week's check-in is in), and
        // this week has none.
        var panel = await NewCommitments().GetPanelAsync();
        Assert.True(panel.IsNextWeek);
        Assert.Equal(NextWeek, panel.WeekStart);
        Assert.Empty(await NewCommitments().GetCurrentWeekAsync());
        var week = panel.Lines;
        Assert.Equal(["Ship it", "Run"], week.Select(c => c.Text));
        Assert.All(week, c => Assert.Equal(CommitmentStatus.Open, c.Status));
        Assert.All(week, c => Assert.Equal(NextWeek, c.WeekStart));
        Assert.All(week, c => Assert.Equal(record.Id, c.CheckInId));
        Assert.Null(week[1].LinkedTaskId); // a link to a task that doesn't exist is dropped
    }

    // ---------------------------------------------------------------- keeping

    [Fact]
    public async Task Linked_commitment_is_kept_when_its_task_is_completed_in_ltfi()
    {
        var outbox = NewOutbox();
        var tasks = new TaskService(_factory, outbox, new RemindersSettings());
        var project = await new ProjectService(_factory).CreateAsync(new ProjectDraft { Title = "Robotics" });
        var task = await tasks.CreateAsync(new TaskDraft { Title = "Flash board", ProjectId = project.Id });

        var commitments = NewCommitments();
        Assert.Contains(await commitments.GetLinkableTasksAsync(), t => t.Id == task.Id);

        await NewReflections().SaveWeeklyCheckInAsync(Answers(), [new CommitmentDraft("Flash the board", task.Id)]);
        var open = Assert.Single(await PanelAsync(commitments));
        Assert.True(open.IsLinked);
        Assert.Equal("Flash board", open.LinkedTaskTitle);
        Assert.True(open.LinkedTaskOpen);

        await tasks.SetStatusAsync(task.Id, TaskStatus.Completed);

        var kept = Assert.Single(await PanelAsync(commitments));
        Assert.Equal(CommitmentStatus.Kept, kept.Status);
        Assert.NotNull(kept.ResolvedAt);
        Assert.DoesNotContain(await commitments.GetLinkableTasksAsync(), t => t.Id == task.Id);

        // Reading again (reconcile runs every read) doesn't add a second evidence item.
        await commitments.GetPanelAsync();
        await commitments.GetCurrentWeekAsync();
        var evidence = Assert.Single(await KeptEvidenceAsync());
        Assert.Equal(project.Id, evidence.ProjectId);
        Assert.Equal(task.Id, evidence.TaskId);
        Assert.Equal(WeeklyCommitments.EvidenceSource, evidence.Source);
    }

    [Fact]
    public async Task Linked_commitment_is_kept_when_the_completion_arrives_via_reminders_sync()
    {
        var sync = new ReminderSyncService(_factory, new FileReminderSource(SnapshotPath), new RemindersSettings());
        WriteExport("""{"id":"r-1","title":"Renew passport","list":"Errands","isCompleted":false,"dueDate":"2026-10-08T09:00:00Z","creationDate":"2026-10-01T09:00:00Z"}""");
        Assert.True((await sync.SyncAsync()).Succeeded);

        var commitments = NewCommitments();
        var linkable = Assert.Single(await commitments.GetLinkableTasksAsync());
        Assert.Equal("Renew passport", linkable.Title);
        Assert.Equal("Errands", linkable.Area);  // unmapped list → Life area
        Assert.NotNull(linkable.DueAt);

        await NewReflections().SaveWeeklyCheckInAsync(Answers(), [new CommitmentDraft("Passport", linkable.Id), new CommitmentDraft("Other")]);

        WriteExport("""{"id":"r-1","title":"Renew passport","list":"Errands","isCompleted":true,"completionDate":"2026-10-05T12:00:00Z","dueDate":"2026-10-08T09:00:00Z","creationDate":"2026-10-01T09:00:00Z"}""");
        Assert.Equal(1, (await sync.SyncAsync()).Completed);

        var week = await PanelAsync(commitments);
        Assert.Equal(CommitmentStatus.Kept, week[0].Status);
        Assert.Equal("Errands", week[0].LinkedArea);
        Assert.Equal(CommitmentStatus.Open, week[1].Status);

        var evidence = Assert.Single(await KeptEvidenceAsync());
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero), evidence.OccurredAt); // the completion time
    }

    [Fact]
    public async Task Manual_keep_writes_exactly_one_five_point_evidence_item()
    {
        await NewReflections().SaveWeeklyCheckInAsync(Answers(), [new CommitmentDraft("Read 2 chapters")]);
        var commitments = NewCommitments();
        var c = Assert.Single(await PanelAsync(commitments));

        await commitments.KeepAsync(c.Id);
        await commitments.KeepAsync(c.Id); // idempotent

        Assert.True(Assert.Single(await PanelAsync(commitments)).IsKept);
        var evidence = Assert.Single(await KeptEvidenceAsync());
        Assert.Equal("Kept: Read 2 chapters", evidence.Title);
        Assert.Equal(5, EvidencePoints.For(evidence.Type));
        Assert.Equal(Saturday, evidence.OccurredAt);

        await Assert.ThrowsAsync<InvalidOperationException>(() => commitments.KeepAsync(Guid.NewGuid()));
    }

    // ---------------------------------------------------------------- next check-in

    [Fact]
    public async Task Next_check_in_reviews_last_week_marks_unresolved_missed_and_carries_over()
    {
        var tasks = new TaskService(_factory, NewOutbox(), new RemindersSettings());
        var task = await tasks.CreateAsync(new TaskDraft { Title = "Write report" });
        var reflections = NewReflections();
        var commitments = NewCommitments();

        await reflections.SaveWeeklyCheckInAsync(Answers(),
            [new CommitmentDraft("Report", task.Id), new CommitmentDraft("Gym"), new CommitmentDraft("Call")]);
        Assert.Empty(await commitments.GetPendingReviewAsync()); // nothing applied to Oct 5–11

        // During Oct 12–18 they are this week's commitments (no longer "next week").
        _clock.Now = Saturday.AddDays(3);
        var during = await commitments.GetPanelAsync();
        Assert.False(during.IsNextWeek);
        Assert.Equal(["Report", "Gym", "Call"], during.Lines.Select(r => r.Text));
        Assert.Equal(["Report", "Gym", "Call"], (await commitments.GetCurrentWeekAsync()).Select(r => r.Text));
        Assert.Empty(await commitments.GetPendingReviewAsync()); // Mon–Fri: week Oct 5 is reviewed, nothing open before Oct 12

        // Saturday Oct 17 the window for Oct 12–18 opens and reviews exactly those three.
        _clock.Now = Saturday.AddDays(7);
        var review = await commitments.GetPendingReviewAsync();
        Assert.Equal(["Report", "Gym", "Call"], review.Select(r => r.Text));
        Assert.All(review, r => Assert.Equal(NextWeek, r.WeekStart));
        var (report, gym, call) = (review[0], review[1], review[2]);
        Assert.True(report.LinkedTaskOpen);

        // Gym was kept (checked in the review), Report is carried over (link kept, task still open),
        // Call is left unresolved.
        await reflections.SaveWeeklyCheckInAsync(Answers(),
            [new CommitmentDraft(report.Text, report.LinkedTaskId), new CommitmentDraft("New thing")],
            new Dictionary<Guid, CommitmentStatus> { [gym.Id] = CommitmentStatus.Kept, [report.Id] = CommitmentStatus.Missed });

        await using (var db = _factory.CreateDbContext())
        {
            var byId = await db.Commitments.ToDictionaryAsync(c => c.Id);
            Assert.Equal(CommitmentStatus.Missed, byId[report.Id].Status);
            Assert.Equal(CommitmentStatus.Kept, byId[gym.Id].Status);
            Assert.Equal(CommitmentStatus.Missed, byId[call.Id].Status);
            Assert.All(new[] { report.Id, gym.Id, call.Id }, id => Assert.NotNull(byId[id].ResolvedAt));
        }

        var kept = Assert.Single(await KeptEvidenceAsync());
        Assert.Equal("Kept: Gym", kept.Title);

        // The new ones apply to Oct 19–25; the panel shows them as next week's.
        var upcoming = await commitments.GetPanelAsync();
        Assert.True(upcoming.IsNextWeek);
        Assert.Equal(["Report", "New thing"], upcoming.Lines.Select(c => c.Text));
        Assert.Equal(task.Id, upcoming.Lines[0].LinkedTaskId);
        Assert.Equal(NextWeek.AddDays(7), upcoming.Lines[0].WeekStart);

        // All resolved and this week's check-in is in: nothing left to review.
        Assert.Empty(await commitments.GetPendingReviewAsync());
    }

    [Fact]
    public async Task Resubmitting_in_the_same_week_drops_the_earlier_open_commitments()
    {
        var reflections = NewReflections();
        var commitments = NewCommitments();
        await reflections.SaveWeeklyCheckInAsync(Answers(), [new CommitmentDraft("A"), new CommitmentDraft("B")]);
        await commitments.KeepAsync((await PanelAsync(commitments))[0].Id);

        // Sunday — same reviewed week (Oct 5–11): a revision.
        _clock.Now = Saturday.AddDays(1).AddHours(5);
        await reflections.SaveWeeklyCheckInAsync(Answers(), [new CommitmentDraft("C")]);

        // A stays (kept), B is superseded, C is new.
        Assert.Equal(["A", "C"], (await PanelAsync(commitments)).Select(c => c.Text));

        // A late check-in on Monday reviews the same week too, so it also supersedes (C → Dropped).
        _clock.Now = Saturday.AddDays(2).AddHours(1);
        await reflections.SaveWeeklyCheckInAsync(Answers(), [new CommitmentDraft("D")]);
        Assert.Equal(["A", "D"], (await commitments.GetCurrentWeekAsync()).Select(c => c.Text));
    }

    // ---------------------------------------------------------------- backfill

    [Fact]
    public async Task Backfill_splits_the_recent_v1_q5_answers_once()
    {
        // v1 check-ins saved before commitments existed: one that set this week's commitments
        // (Sat Oct 3 → reviewed Sep 28, applies Oct 5), one that set next week's (today, applies
        // Oct 12), and an older one (Sat Sep 26 → applies Sep 28) that is never back-filled.
        static List<WeeklyCheckInAnswer> V1(string q5)
        {
            var answers = WeeklyCheckIn.Questions.Select(q => new WeeklyCheckInAnswer(q, "x")).ToList();
            answers[WeeklyCheckIn.CommitmentsQuestionIndex] = answers[WeeklyCheckIn.CommitmentsQuestionIndex] with { Answer = q5 };
            return answers;
        }

        await using (var db = _factory.CreateDbContext())
        {
            foreach (var (at, q5) in new[]
                     {
                         (Saturday.AddDays(-14), "Old one"),
                         (Saturday.AddDays(-7), "Last week's plan"),
                         (Saturday.AddMinutes(-1), "1. Finish LTFI commitments\n2. • Run twice\n- Email advisor\n4. Overflow")
                     })
            {
                db.Reflections.Add(new ReflectionEntry
                {
                    ScopeType = ReflectionScope.Week, Prompt = WeeklyCheckIn.PromptVersion,
                    Body = WeeklyCheckIn.Serialize(V1(q5)), CreatedAt = at
                });
            }
            await db.SaveChangesAsync();
        }

        var commitments = NewCommitments();
        Assert.Equal(4, await commitments.BackfillCurrentWeekAsync());
        Assert.Equal(0, await commitments.BackfillCurrentWeekAsync()); // idempotent

        var thisWeek = await commitments.GetCurrentWeekAsync();       // also backfills; nothing new
        Assert.Equal(["Last week's plan"], thisWeek.Select(c => c.Text));
        Assert.All(thisWeek, c => Assert.Equal(ThisWeek, c.WeekStart));

        var next = await PanelAsync(commitments);
        Assert.Equal(["Finish LTFI commitments", "Run twice", "Email advisor"], next.Select(c => c.Text));
        Assert.All(next, c => Assert.Null(c.LinkedTaskId));
        Assert.All(next, c => Assert.Equal(NextWeek, c.WeekStart));

        await using (var db = _factory.CreateDbContext())
        {
            Assert.Equal(4, await db.Commitments.CountAsync()); // the older check-in is left alone
        }
    }

    // ---------------------------------------------------------------- 2026-10-11 model migration

    [Fact]
    public async Task Owner_legacy_rows_are_reinterpreted_to_the_week_they_apply_to_idempotently()
    {
        // The owner's real data: Sun Oct 4 18:40 and Sat Oct 10 12:54 EDT check-ins, whose rows
        // the old model both stamped with the Sunday-start week "2026-10-04".
        var edt = TimeSpan.FromHours(-4);
        var clock = new FixedZoneClock(new DateTimeOffset(2026, 10, 11, 19, 0, 0, edt), edt); // Sun Oct 11, after 18:00
        var first = new ReflectionEntry
        {
            ScopeType = ReflectionScope.Week, Prompt = WeeklyCheckIn.PromptVersion,
            Body = WeeklyCheckIn.Serialize(WeeklyCheckIn.BuildAnswers(["", "", "", "", "nsdr, wake up earlier, sleep earlier.", ""])),
            CreatedAt = new DateTimeOffset(2026, 10, 4, 18, 40, 19, edt)
        };
        var second = new ReflectionEntry
        {
            ScopeType = ReflectionScope.Week, Prompt = WeeklyCheckIn.PromptVersion,
            Body = WeeklyCheckIn.Serialize(WeeklyCheckIn.BuildAnswers(["homework done", "", "", "", "1. for her\n2. lock in for this math test\n3. autocad is useful", ""])),
            CreatedAt = new DateTimeOffset(2026, 10, 10, 12, 54, 0, edt)
        };
        var legacyWeek = new DateOnly(2026, 10, 4);
        await using (var db = _factory.CreateDbContext())
        {
            db.Reflections.AddRange(first, second);
            db.Commitments.Add(new WeeklyCommitment
            {
                CheckInId = first.Id, WeekStart = legacyWeek, Text = "nsdr, wake up earlier, sleep earlier.",
                Status = CommitmentStatus.Kept, ResolvedAt = new DateTimeOffset(2026, 10, 7, 1, 38, 9, edt), CreatedAt = first.CreatedAt
            });
            foreach (var (text, i) in new[] { "for her", "lock in for this math test", "autocad is useful" }.Select((t, i) => (t, i)))
            {
                db.Commitments.Add(new WeeklyCommitment
                {
                    CheckInId = second.Id, WeekStart = legacyWeek, Text = text, SortOrder = i, CreatedAt = second.CreatedAt
                });
            }
            await db.SaveChangesAsync();
        }

        var commitments = new CommitmentService(_factory, clock);

        // This week (Oct 5–11) had the Oct 4 commitment; the Oct 10 check-in has reviewed it, so the
        // panel shows the Oct 10 commitments as next week's (Oct 12–18).
        Assert.Equal(["nsdr, wake up earlier, sleep earlier."], (await commitments.GetCurrentWeekAsync()).Select(c => c.Text));
        var panel = await commitments.GetPanelAsync();
        Assert.True(panel.IsNextWeek);
        Assert.Equal(new DateOnly(2026, 10, 12), panel.WeekStart);
        Assert.Equal(["for her", "lock in for this math test", "autocad is useful"], panel.Lines.Select(c => c.Text));
        Assert.Empty(await commitments.GetPendingReviewAsync());

        await using (var db = _factory.CreateDbContext())
        {
            var rows = await db.Commitments.ToListAsync();
            Assert.Equal(new DateOnly(2026, 10, 5), rows.Single(c => c.CheckInId == first.Id).WeekStart);
            Assert.All(rows.Where(c => c.CheckInId == second.Id), c => Assert.Equal(new DateOnly(2026, 10, 12), c.WeekStart));
            Assert.Equal(4, rows.Count); // nothing back-filled on top (both check-ins already had rows)
        }

        // Idempotent: reading again changes nothing; the status is Done with no gate.
        var again = await commitments.GetPanelAsync();
        Assert.Equal(panel.Lines.Select(l => (l.Id, l.WeekStart)), again.Lines.Select(l => (l.Id, l.WeekStart)));
        var status = await new ReflectionService(_factory, new JsonCheckInSnoozeStore(Path.Combine(_folder, "snooze.json")), clock)
            .GetWeeklyCheckInStatusAsync();
        Assert.Equal(CheckInPhase.Done, status.Phase);
        Assert.False(status.MustShow);

        // Next Saturday the Oct 12–18 review lists exactly the Oct 10 commitments.
        clock.Now = new DateTimeOffset(2026, 10, 17, 9, 0, 0, edt);
        Assert.Equal(["for her", "lock in for this math test", "autocad is useful"],
            (await commitments.GetPendingReviewAsync()).Select(c => c.Text));
    }

    // ---------------------------------------------------------------- graph click

    [Fact]
    public async Task Evidence_for_a_single_local_day_is_returned_newest_first()
    {
        var day = new DateOnly(2026, 10, 3);
        DateTimeOffset Local(int d, int h, int m) => new(new DateTime(2026, 10, d, h, m, 0, DateTimeKind.Local));

        await using (var db = _factory.CreateDbContext())
        {
            db.Evidence.Add(new EvidenceItem { Type = EvidenceType.TaskCompleted, Title = "late Fri", OccurredAt = Local(2, 23, 59) });
            db.Evidence.Add(new EvidenceItem { Type = EvidenceType.TaskCompleted, Title = "early Sat", OccurredAt = Local(3, 0, 0) });
            db.Evidence.Add(new EvidenceItem { Type = EvidenceType.FocusSessionCompleted, Title = "late Sat", OccurredAt = Local(3, 23, 59) });
            db.Evidence.Add(new EvidenceItem { Type = EvidenceType.TaskCompleted, Title = "Sun", OccurredAt = Local(4, 0, 1) });
            await db.SaveChangesAsync();
        }

        var lines = await new EvidenceService(_factory).GetForDayAsync(day);
        Assert.Equal(["late Sat", "early Sat"], lines.Select(l => l.Title));
        Assert.Equal(15, lines.Sum(l => EvidencePoints.ForContribution(l.Type)));
        Assert.Empty(await new EvidenceService(_factory).GetForDayAsync(day.AddDays(-30)));
    }

    // ---------------------------------------------------------------- standing progress

    [Fact]
    public async Task Standing_projects_have_no_progress()
    {
        var projects = new ProjectService(_factory);
        var tasks = new TaskService(_factory);
        var life = await projects.CreateAsync(new ProjectDraft { Title = "Life", IsStanding = true });
        var real = await projects.CreateAsync(new ProjectDraft { Title = "Robotics" });
        await tasks.CreateAsync(new TaskDraft { Title = "done", ProjectId = real.Id, Status = TaskStatus.Completed });

        await using (var db = _factory.CreateDbContext())
        {
            // A completed task in Life would otherwise make it "100%".
            db.Tasks.Add(new TaskItem { Title = "groceries", ProjectId = life.Id, Status = TaskStatus.Completed });
            await db.SaveChangesAsync();
        }

        var lifeLoaded = (await projects.GetByIdAsync(life.Id))!;
        Assert.False(lifeLoaded.HasProgress);
        Assert.Null(lifeLoaded.ProgressPercent);
        Assert.Null(ProjectProgress.For(lifeLoaded));

        var realLoaded = (await projects.GetByIdAsync(real.Id))!;
        Assert.True(realLoaded.HasProgress);
        Assert.Equal(100, realLoaded.ProgressPercent);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
