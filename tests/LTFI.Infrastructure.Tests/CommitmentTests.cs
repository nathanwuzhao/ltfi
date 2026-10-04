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
    // Sunday 2026-10-04 18:40 (UTC-pinned clock): its check-in week starts that day at 00:00.
    private static readonly DateTimeOffset Sunday = new(2026, 10, 4, 18, 40, 0, TimeSpan.Zero);
    private static readonly DateOnly ThisWeek = new(2026, 10, 4);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"ltfi-cm-{Guid.NewGuid():N}");
    private readonly TestDbFactory _factory;
    private readonly Clock _clock = new(Sunday);

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
        Assert.Equal(ThisWeek, WeeklyCommitments.WeekOf(Sunday));
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

        var week = await NewCommitments().GetCurrentWeekAsync();
        Assert.Equal(["Ship it", "Run"], week.Select(c => c.Text));
        Assert.All(week, c => Assert.Equal(CommitmentStatus.Open, c.Status));
        Assert.All(week, c => Assert.Equal(ThisWeek, c.WeekStart));
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
        var open = Assert.Single(await commitments.GetCurrentWeekAsync());
        Assert.True(open.IsLinked);
        Assert.Equal("Flash board", open.LinkedTaskTitle);
        Assert.True(open.LinkedTaskOpen);

        await tasks.SetStatusAsync(task.Id, TaskStatus.Completed);

        var kept = Assert.Single(await commitments.GetCurrentWeekAsync());
        Assert.Equal(CommitmentStatus.Kept, kept.Status);
        Assert.NotNull(kept.ResolvedAt);
        Assert.DoesNotContain(await commitments.GetLinkableTasksAsync(), t => t.Id == task.Id);

        // Reading again (reconcile runs every read) doesn't add a second evidence item.
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

        var week = await commitments.GetCurrentWeekAsync();
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
        var c = Assert.Single(await commitments.GetCurrentWeekAsync());

        await commitments.KeepAsync(c.Id);
        await commitments.KeepAsync(c.Id); // idempotent

        Assert.True(Assert.Single(await commitments.GetCurrentWeekAsync()).IsKept);
        var evidence = Assert.Single(await KeptEvidenceAsync());
        Assert.Equal("Kept: Read 2 chapters", evidence.Title);
        Assert.Equal(5, EvidencePoints.For(evidence.Type));
        Assert.Equal(Sunday, evidence.OccurredAt);

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
        Assert.Empty(await commitments.GetPendingReviewAsync()); // nothing from earlier weeks

        // A week later the check-in is due again and reviews last week's three.
        _clock.Now = Sunday.AddDays(7);
        var review = await commitments.GetPendingReviewAsync();
        Assert.Equal(["Report", "Gym", "Call"], review.Select(r => r.Text));
        Assert.Empty(await commitments.GetCurrentWeekAsync());
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

        var thisWeek = await commitments.GetCurrentWeekAsync();
        Assert.Equal(["Report", "New thing"], thisWeek.Select(c => c.Text));
        Assert.Equal(task.Id, thisWeek[0].LinkedTaskId);
        Assert.Equal(ThisWeek.AddDays(7), thisWeek[0].WeekStart);

        // All resolved and this week's check-in is in: nothing left to review.
        Assert.Empty(await commitments.GetPendingReviewAsync());
    }

    [Fact]
    public async Task Resubmitting_in_the_same_week_drops_the_earlier_open_commitments()
    {
        var reflections = NewReflections();
        var commitments = NewCommitments();
        await reflections.SaveWeeklyCheckInAsync(Answers(), [new CommitmentDraft("A"), new CommitmentDraft("B")]);
        await commitments.KeepAsync((await commitments.GetCurrentWeekAsync())[0].Id);

        _clock.Now = Sunday.AddHours(1);
        await reflections.SaveWeeklyCheckInAsync(Answers(), [new CommitmentDraft("C")]);

        // A stays (kept), B is superseded, C is new.
        Assert.Equal(["A", "C"], (await commitments.GetCurrentWeekAsync()).Select(c => c.Text));
    }

    // ---------------------------------------------------------------- backfill

    [Fact]
    public async Task Backfill_splits_this_weeks_v1_q5_answer_once()
    {
        // A v1 check-in saved this week before commitments existed, and one from last week.
        var answers = WeeklyCheckIn.Questions.Select(q => new WeeklyCheckInAnswer(q, "x")).ToList();
        answers[WeeklyCheckIn.CommitmentsQuestionIndex] = answers[WeeklyCheckIn.CommitmentsQuestionIndex] with
        {
            Answer = "1. Finish LTFI commitments\n2. • Run twice\n- Email advisor\n4. Overflow"
        };
        await using (var db = _factory.CreateDbContext())
        {
            db.Reflections.Add(new ReflectionEntry
            {
                ScopeType = ReflectionScope.Week, Prompt = WeeklyCheckIn.PromptVersion,
                Body = WeeklyCheckIn.Serialize(answers), CreatedAt = Sunday.AddDays(-7)
            });
            db.Reflections.Add(new ReflectionEntry
            {
                ScopeType = ReflectionScope.Week, Prompt = WeeklyCheckIn.PromptVersion,
                Body = WeeklyCheckIn.Serialize(answers), CreatedAt = Sunday.AddMinutes(-1)
            });
            await db.SaveChangesAsync();
        }

        var commitments = NewCommitments();
        Assert.Equal(3, await commitments.BackfillCurrentWeekAsync());
        Assert.Equal(0, await commitments.BackfillCurrentWeekAsync()); // idempotent

        var week = await commitments.GetCurrentWeekAsync();           // also backfills; still 3
        Assert.Equal(["Finish LTFI commitments", "Run twice", "Email advisor"], week.Select(c => c.Text));
        Assert.All(week, c => Assert.Null(c.LinkedTaskId));

        await using (var db = _factory.CreateDbContext())
        {
            Assert.Equal(3, await db.Commitments.CountAsync()); // last week's check-in is left alone
        }
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
