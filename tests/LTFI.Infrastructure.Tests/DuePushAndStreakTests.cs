using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using LTFI.Infrastructure.Reminders;
using LTFI.Infrastructure.Services;
using LTFI.Infrastructure.Settings;
using Xunit;

namespace LTFI.Infrastructure.Tests;

/// <summary>
/// Pushing a reminder's due date from LTFI (outbox "update": coalescing, pending-create edits, sync
/// not clobbering it, confirmation), and the header's activity streak = the graph's current streak.
/// Real SQLite + real files in a temp folder.
/// </summary>
public sealed class DuePushAndStreakTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"ltfi-due-{Guid.NewGuid():N}");
    private readonly TestDbFactory _factory;

    public DuePushAndStreakTests()
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

    private string SnapshotPath => Path.Combine(_folder, "reminders.jsonl.json");
    private string OutboxPath => Path.Combine(_folder, "outbox.json");

    private ReminderOutbox NewOutbox() => new(_factory, () => OutboxPath);

    private TaskService NewTasks(ReminderOutbox? outbox = null) => new(_factory, outbox, new RemindersSettings());

    private ReminderSyncService NewSync(ReminderOutbox? outbox = null) =>
        new(_factory, new FileReminderSource(SnapshotPath), new RemindersSettings(), outbox);

    private void WriteExport(string remindersJson)
    {
        File.WriteAllText(SnapshotPath, $$"""{"schema":"ltfi.reminders/v1","reminders":[{{remindersJson}}]}""");
        File.SetLastWriteTimeUtc(SnapshotPath, DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 100000)));
    }

    private JsonElement[] ReadOutboxCommands()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(OutboxPath));
        Assert.Equal("ltfi.outbox/v1", doc.RootElement.GetProperty("schema").GetString());
        return doc.RootElement.GetProperty("commands").EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    private static DateTimeOffset Local(int y, int m, int d, int h = 0, int min = 0)
    {
        var dt = new DateTime(y, m, d, h, min, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
    }

    private static string Iso(DateTimeOffset value) => ReminderOutbox.FormatDue(value);

    /// <summary>A reminder the iPhone already has (its create confirmed), with a due date.</summary>
    private async Task<(TaskItem Task, ReminderOutbox Outbox, TaskService Tasks, ReminderSyncService Sync)> SyncedReminderAsync(
        DateTimeOffset due)
    {
        var outbox = NewOutbox();
        var tasks = NewTasks(outbox);
        var sync = NewSync(outbox);
        var task = await tasks.CreateAsync(new TaskDraft { Title = "Pay rent", DueAt = due });
        WriteExport($$"""{"title":"Pay rent","list":"LTFI","url":"{{task.ExternalId}}","dueDate":"{{Iso(due)}}","creationDate":"2026-10-01T09:00:00Z"}""");
        await sync.SyncAsync();
        Assert.Equal(0, await outbox.CountPendingAsync());
        return ((await tasks.GetByIdAsync(task.Id))!, outbox, tasks, sync);
    }

    // ---------------------------------------------------------------- queue + coalesce

    [Fact]
    public async Task Setting_a_due_date_queues_one_update_and_a_newer_date_replaces_it()
    {
        var (task, outbox, tasks, _) = await SyncedReminderAsync(Local(2026, 10, 10, 9, 30));

        await tasks.SetDueDateAsync(task.Id, Local(2026, 10, 12, 9, 30));
        var command = Assert.Single(ReadOutboxCommands());
        Assert.Equal("update", command.GetProperty("op").GetString());
        Assert.Equal(task.ExternalId, command.GetProperty("url").GetString());
        Assert.Equal(Iso(Local(2026, 10, 12, 9, 30)), command.GetProperty("dueDate").GetString());
        // Flat strings, exactly op/url/dueDate, full ISO 8601 with offset.
        Assert.Equal(["op", "url", "dueDate"], command.EnumerateObject().Select(p => p.Name));
        Assert.All(command.EnumerateObject(), p => Assert.Equal(JsonValueKind.String, p.Value.ValueKind));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2}$", command.GetProperty("dueDate").GetString()!);

        var pending = (await tasks.GetByIdAsync(task.Id))!;
        Assert.True(pending.IsPendingOnPhone);
        Assert.Equal(Local(2026, 10, 12, 9, 30), pending.DueAt);

        // A newer date replaces the queued one: still a single update.
        await tasks.PushDueByDaysAsync(task.Id, 1);
        command = Assert.Single(ReadOutboxCommands());
        Assert.Equal(Iso(Local(2026, 10, 13, 9, 30)), command.GetProperty("dueDate").GetString());
        Assert.Equal(1, await outbox.CountPendingAsync());
    }

    [Fact]
    public async Task Due_change_while_the_create_is_pending_edits_the_create_instead()
    {
        var outbox = NewOutbox();
        var tasks = NewTasks(outbox);
        var task = await tasks.CreateAsync(new TaskDraft { Title = "Order filament", DueAt = Local(2026, 10, 10) });

        await tasks.PushDueByDaysAsync(task.Id, 1);

        var command = Assert.Single(ReadOutboxCommands());
        Assert.Equal("create", command.GetProperty("op").GetString());
        Assert.Equal("Order filament", command.GetProperty("title").GetString());
        Assert.Equal(Iso(Local(2026, 10, 11)), command.GetProperty("dueDate").GetString());

        // Same through the editor.
        await tasks.UpdateAsync(task.Id, new TaskDraft { Title = "Order filament", DueAt = Local(2026, 10, 20) });
        command = Assert.Single(ReadOutboxCommands());
        Assert.Equal("create", command.GetProperty("op").GetString());
        Assert.Equal(Iso(Local(2026, 10, 20)), command.GetProperty("dueDate").GetString());
    }

    [Fact]
    public async Task Editor_due_change_on_a_synced_reminder_goes_out_as_an_update()
    {
        var (task, _, tasks, _) = await SyncedReminderAsync(Local(2026, 10, 10));

        await tasks.UpdateAsync(task.Id, new TaskDraft
        {
            Title = task.Title, ProjectId = task.ProjectId, AreaId = task.AreaId, DueAt = Local(2026, 10, 15)
        });
        var command = Assert.Single(ReadOutboxCommands());
        Assert.Equal("update", command.GetProperty("op").GetString());
        Assert.Equal(Iso(Local(2026, 10, 15)), command.GetProperty("dueDate").GetString());

        // Clearing a due date can't be pushed.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tasks.UpdateAsync(task.Id, new TaskDraft
        {
            Title = task.Title, ProjectId = task.ProjectId, AreaId = task.AreaId, DueAt = null
        }));
        Assert.Equal(TaskService.CannotClearDueMessage, ex.Message);
        Assert.Equal(Local(2026, 10, 15), (await tasks.GetByIdAsync(task.Id))!.DueAt);

        // An unchanged due date queues nothing new.
        await tasks.UpdateAsync(task.Id, new TaskDraft
        {
            Title = task.Title, ProjectId = task.ProjectId, AreaId = task.AreaId, DueAt = Local(2026, 10, 15)
        });
        Assert.Single(ReadOutboxCommands());
    }

    // ---------------------------------------------------------------- sync: hold, then confirm

    [Fact]
    public async Task Sync_keeps_the_pushed_due_date_until_an_export_shows_it_then_the_phone_owns_it_again()
    {
        var original = Local(2026, 10, 10, 9, 30);
        var (task, outbox, tasks, sync) = await SyncedReminderAsync(original);
        var url = task.ExternalId!;
        var pushed = await tasks.PushDueByDaysAsync(task.Id, 1);

        // The iPhone hasn't applied it yet: the export still has the old date, which must not win.
        WriteExport($$"""{"title":"Pay rent","list":"LTFI","url":"{{url}}","dueDate":"{{Iso(original)}}","creationDate":"2026-10-01T09:00:00Z"}""");
        var first = await sync.SyncAsync();
        Assert.Equal(0, first.Confirmed);
        var held = (await tasks.GetByIdAsync(task.Id))!;
        Assert.Equal(pushed, held.DueAt);
        Assert.True(held.IsPendingOnPhone);
        Assert.Single(ReadOutboxCommands());

        // The export shows the new date (seconds dropped, written in UTC): confirmed.
        var asUtc = pushed.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture);
        WriteExport($$"""{"title":"Pay rent","list":"LTFI","url":"{{url}}","dueDate":"{{asUtc}}","creationDate":"2026-10-01T09:00:00Z"}""");
        var second = await sync.SyncAsync();
        Assert.Equal(1, second.Confirmed);
        Assert.Empty(ReadOutboxCommands());
        Assert.Equal(0, await outbox.CountPendingAsync());
        var confirmed = (await tasks.GetByIdAsync(task.Id))!;
        Assert.False(confirmed.IsPendingOnPhone);
        Assert.Equal(pushed, confirmed.DueAt);

        // From now on the phone owns the due date again.
        WriteExport($$"""{"title":"Pay rent","list":"LTFI","url":"{{url}}","dueDate":"{{Iso(Local(2026, 10, 20, 8, 0))}}","creationDate":"2026-10-01T09:00:00Z"}""");
        await sync.SyncAsync();
        Assert.Equal(Local(2026, 10, 20, 8, 0), (await tasks.GetByIdAsync(task.Id))!.DueAt);
    }

    [Fact]
    public async Task An_all_day_reminder_confirms_a_pushed_date_on_the_same_local_day()
    {
        var (task, _, tasks, sync) = await SyncedReminderAsync(Local(2026, 10, 10));
        var pushed = await tasks.PushDueByDaysAsync(task.Id, 1);
        Assert.Equal(Local(2026, 10, 11), pushed); // midnight stays midnight

        // The export writes it date-only (all-day reminder).
        WriteExport($$"""{"title":"Pay rent","list":"LTFI","url":"{{task.ExternalId}}","dueDate":"2026-10-11","creationDate":"2026-10-01T09:00:00Z"}""");
        Assert.Equal(1, (await sync.SyncAsync()).Confirmed);
        Assert.Empty(ReadOutboxCommands());
        Assert.False((await tasks.GetByIdAsync(task.Id))!.IsPendingOnPhone);
    }

    [Fact]
    public void Due_matching_tolerates_seconds_offsets_and_date_only_but_not_other_days()
    {
        var pushed = Local(2026, 10, 11, 9, 30);
        Assert.True(ReminderRules.DueMatches(pushed, pushed.AddSeconds(30)));
        Assert.True(ReminderRules.DueMatches(pushed, pushed.ToUniversalTime()));
        Assert.True(ReminderRules.DueMatches(pushed, Local(2026, 10, 11)));          // phone shows the day only
        Assert.True(ReminderRules.DueMatches(Local(2026, 10, 11), Local(2026, 10, 11, 9, 0))); // pushed all-day
        Assert.False(ReminderRules.DueMatches(pushed, Local(2026, 10, 11, 10, 30)));
        Assert.False(ReminderRules.DueMatches(pushed, Local(2026, 10, 12)));
        Assert.False(ReminderRules.DueMatches(pushed, null));
    }

    [Fact]
    public async Task A_create_applied_from_a_stale_outbox_sends_the_newer_due_date_as_an_update()
    {
        var outbox = NewOutbox();
        var tasks = NewTasks(outbox);
        var sync = NewSync(outbox);
        var task = await tasks.CreateAsync(new TaskDraft { Title = "Renew passport", DueAt = Local(2026, 10, 10) });
        await tasks.PushDueByDaysAsync(task.Id, 2); // create payload now says the 12th

        // The iPhone made it from the older outbox.json (still the 10th).
        WriteExport($$"""{"title":"Renew passport","list":"LTFI","url":"{{task.ExternalId}}","dueDate":"2026-10-10","creationDate":"2026-10-01T09:00:00Z"}""");
        var result = await sync.SyncAsync();
        Assert.Equal(1, result.Confirmed);

        var command = Assert.Single(ReadOutboxCommands());
        Assert.Equal("update", command.GetProperty("op").GetString());
        Assert.Equal(Iso(Local(2026, 10, 12)), command.GetProperty("dueDate").GetString());
        var current = (await tasks.GetByIdAsync(task.Id))!;
        Assert.Equal(Local(2026, 10, 12), current.DueAt);
        Assert.True(current.IsPendingOnPhone);
    }

    // ---------------------------------------------------------------- rules

    [Fact]
    public void Push_keeps_the_time_of_day_and_counts_from_today_without_a_due_date()
    {
        var today = new DateTime(2026, 10, 6);
        Assert.Equal(Local(2026, 10, 11, 17, 45), TaskService.ShiftDue(Local(2026, 10, 10, 17, 45), 1, today));
        Assert.Equal(Local(2026, 10, 11), TaskService.ShiftDue(Local(2026, 10, 10), 1, today));
        Assert.Equal(Local(2026, 10, 7), TaskService.ShiftDue(null, 1, today));
        // Across a DST change the local wall-clock time is kept (US: Nov 1 2026).
        var shifted = TaskService.ShiftDue(Local(2026, 10, 31, 9, 0), 2, today);
        Assert.Equal(new DateTime(2026, 11, 2, 9, 0, 0), shifted.LocalDateTime);
    }

    [Fact]
    public async Task Pushing_a_reminder_without_an_ltfi_url_is_blocked()
    {
        var outbox = NewOutbox();
        var tasks = NewTasks(outbox);
        WriteExport("""{"title":"Unstamped","list":"TODO GENERAL","dueDate":"2026-10-10","creationDate":"2026-10-01T09:00:00Z"}""");
        await NewSync(outbox).SyncAsync();

        Guid id;
        await using (var db = _factory.CreateDbContext()) { id = (await db.Tasks.SingleAsync()).Id; }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tasks.PushDueByDaysAsync(id, 1));
        Assert.Equal(TaskService.NoLtfiIdDueMessage, ex.Message);
        Assert.Contains("no LTFI id yet", ex.Message);
        Assert.Equal(Local(2026, 10, 10), (await tasks.GetByIdAsync(id))!.DueAt);
        Assert.Equal(0, await outbox.CountPendingAsync());
    }

    // ---------------------------------------------------------------- header streak

    [Fact]
    public async Task Activity_streak_matches_the_contribution_graph_not_the_focus_streak()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        await using (var db = _factory.CreateDbContext())
        {
            // Activity today, yesterday and the day before (no focus sessions); a gap; then older.
            foreach (var (daysAgo, type) in new[]
                     {
                         (0, EvidenceType.TaskCompleted), (1, EvidenceType.CommitmentKept),
                         (2, EvidenceType.NsdrCompleted), (4, EvidenceType.TaskCompleted),
                         // A distraction signal isn't a contribution: it doesn't bridge the gap.
                         (3, EvidenceType.DistractionBlocked)
                     })
            {
                db.Evidence.Add(new EvidenceItem
                {
                    Type = type, Source = "test", Title = "x",
                    OccurredAt = new DateTimeOffset(DateTime.Today.AddDays(-daysAgo).AddHours(12))
                });
            }
            await db.SaveChangesAsync();
        }

        var insights = new InsightsService(_factory);
        var streak = await insights.GetActivityStreakAsync();
        var snapshot = await insights.GetTodaySnapshotAsync();
        var graph = ContributionGraph.Build(await new EvidenceService(_factory).GetDailyScoresAsync(ContributionGraph.DefaultDays), today);

        Assert.Equal(3, streak);
        Assert.Equal(graph.Stats.CurrentStreak, streak);
        Assert.Equal(streak, snapshot.ActivityStreakDays);
        Assert.Equal(0, snapshot.FocusStreakDays);
    }
}
