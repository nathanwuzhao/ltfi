using System;
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
using TaskStatus = LTFI.Core.Domain.TaskStatus;

namespace LTFI.Infrastructure.Tests;

/// <summary>
/// Areas, the standing-project exemption, and LTFI → iPhone write-back (outbox.json): tasks are
/// always reminder-backed, creates/completes are queued, written atomically, and confirmed by a
/// later export. Real SQLite + real files in a temp folder (the stand-in for iCloud Drive\LTFI).
/// </summary>
public sealed class WriteBackTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"ltfi-wb-{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly TestDbFactory _factory;

    public WriteBackTests()
    {
        Directory.CreateDirectory(_folder);
        _dbPath = Path.Combine(_folder, "test.db");
        _factory = new TestDbFactory(_dbPath);
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
        Assert.True(DateTimeOffset.TryParse(doc.RootElement.GetProperty("writtenAt").GetString(), out _));
        return doc.RootElement.GetProperty("commands").EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    // ---------------------------------------------------------------- areas + standing

    [Fact]
    public async Task Areas_can_be_added_renamed_and_removed_and_names_are_unique_per_project()
    {
        var projects = new ProjectService(_factory);
        var areas = new AreaService(_factory);
        var robotics = await projects.CreateAsync(new ProjectDraft { Title = "Robotics" });
        var other = await projects.CreateAsync(new ProjectDraft { Title = "Other" });

        var firmware = await areas.CreateAsync(robotics.Id, " firmware ");
        await areas.CreateAsync(robotics.Id, "mcad");
        await areas.CreateAsync(other.Id, "firmware"); // same name, different project: fine

        Assert.Equal(["firmware", "mcad"], (await areas.GetByProjectAsync(robotics.Id)).Select(a => a.Name));
        await Assert.ThrowsAsync<InvalidOperationException>(() => areas.CreateAsync(robotics.Id, "MCAD"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => areas.CreateAsync(robotics.Id, "  "));

        await areas.RenameAsync(firmware.Id, "ecad");
        await Assert.ThrowsAsync<InvalidOperationException>(() => areas.RenameAsync(firmware.Id, "mcad"));
        Assert.Equal(["ecad", "mcad"], (await areas.GetByProjectAsync(robotics.Id)).Select(a => a.Name));

        // Removing an area keeps its tasks (in the project, with no area).
        var task = await NewTasks().CreateAsync(new TaskDraft { Title = "Flash board", ProjectId = robotics.Id, AreaId = firmware.Id });
        await areas.DeleteAsync(firmware.Id);
        var reloaded = await NewTasks().GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        Assert.Null(reloaded!.AreaId);
        Assert.Equal(robotics.Id, reloaded.ProjectId);
        Assert.Equal(["mcad"], (await areas.GetByProjectAsync(robotics.Id)).Select(a => a.Name));
    }

    [Fact]
    public async Task Standing_projects_are_exempt_from_the_limit_the_meter_and_stalled_detection()
    {
        var projects = new ProjectService(_factory);
        var life = await projects.CreateAsync(new ProjectDraft { Title = "Life", IsStanding = true });
        for (var i = 1; i <= ProjectPolicy.MaxActiveProjects; i++)
        {
            await projects.CreateAsync(new ProjectDraft { Title = $"P{i}" });
        }

        Assert.Equal(ProjectPolicy.MaxActiveProjects, await projects.CountActiveAsync());
        await Assert.ThrowsAsync<ActiveProjectLimitException>(() => projects.CreateAsync(new ProjectDraft { Title = "Fifth" }));
        // Another standing project still fits.
        await projects.CreateAsync(new ProjectDraft { Title = "Health", IsStanding = true });

        // Turning a standing project into a normal one makes it count, so the limit applies.
        await Assert.ThrowsAsync<ActiveProjectLimitException>(
            () => projects.UpdateAsync(life.Id, new ProjectDraft { Title = "Life", IsStanding = false }));

        // An old standing project is never "stalled".
        var old = DateTimeOffset.Now.AddDays(-(ProjectPolicy.StaleAfterDays + 5));
        await using (var db = _factory.CreateDbContext())
        {
            foreach (var p in await db.Projects.ToListAsync())
            {
                p.CreatedAt = old;
            }
            await db.SaveChangesAsync();
        }

        var review = await new ReviewService(_factory).GetWeeklyReviewAsync();
        Assert.Equal(ProjectPolicy.MaxActiveProjects, review.ActiveProjectCount);
        Assert.False(review.IsOverLimit);
        Assert.DoesNotContain(review.StalledProjects, s => s.Title is "Life" or "Health");
        Assert.Contains(review.StalledProjects, s => s.Title == "P1");
    }

    // ---------------------------------------------------------------- create → outbox

    [Fact]
    public async Task There_is_no_local_only_task_creation_every_task_is_reminder_backed()
    {
        var tasks = NewTasks();
        var project = await new ProjectService(_factory).CreateAsync(new ProjectDraft { Title = "Robotics" });

        var loose = await tasks.CreateAsync(new TaskDraft { Title = "Loose" });
        var scoped = await tasks.CreateAsync(new TaskDraft { Title = "Scoped", ProjectId = project.Id });

        await using var db = _factory.CreateDbContext();
        foreach (var task in await db.Tasks.ToListAsync())
        {
            Assert.Equal(ReminderRules.SourceKey, task.ExternalSource);
            Assert.True(ReminderRules.IsLtfiCreatedUrl(task.ExternalId), task.ExternalId);
            Assert.Equal("LTFI", task.ExternalList);       // non-standing project (or none) → ltfiList
            Assert.Equal(TaskStatus.Ready, task.Status);
            Assert.NotNull(task.ExternalPendingSince);
            Assert.True(task.IsPendingOnPhone);
        }

        var commands = await db.Outbox.ToListAsync();
        Assert.Equal(2, commands.Count);
        Assert.All(commands, c => Assert.Equal(OutboxCommand.CreateOp, c.Op));
        Assert.Equal(new[] { loose.ExternalId, scoped.ExternalId }.Order(), commands.Select(c => c.ExternalUrl).Order());
    }

    [Fact]
    public async Task Standing_project_tasks_need_an_area_which_becomes_the_list()
    {
        var projects = new ProjectService(_factory);
        var areas = new AreaService(_factory);
        var life = await projects.CreateAsync(new ProjectDraft { Title = "Life", IsStanding = true });
        var gatech = await areas.CreateAsync(life.Id, "GATECH");
        var robotics = await projects.CreateAsync(new ProjectDraft { Title = "Robotics" });
        var firmware = await areas.CreateAsync(robotics.Id, "firmware");
        var tasks = NewTasks();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tasks.CreateAsync(new TaskDraft { Title = "No area", ProjectId = life.Id }));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tasks.CreateAsync(new TaskDraft { Title = "Wrong area", ProjectId = life.Id, AreaId = firmware.Id }));

        var lifeTask = await tasks.CreateAsync(new TaskDraft { Title = "Read ch 3", ProjectId = life.Id, AreaId = gatech.Id });
        var fwTask = await tasks.CreateAsync(new TaskDraft { Title = "Flash", ProjectId = robotics.Id, AreaId = firmware.Id });

        Assert.Equal("GATECH", lifeTask.ExternalList);
        Assert.Equal("LTFI", fwTask.ExternalList);
        await using var db = _factory.CreateDbContext();
        Assert.Equal(2, await db.Outbox.CountAsync()); // the rejected drafts queued nothing
    }

    [Fact]
    public async Task Outbox_file_is_written_atomically_with_flat_string_commands()
    {
        var outbox = NewOutbox();
        var tasks = NewTasks(outbox);
        var due = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 10)));

        var task = await tasks.CreateAsync(new TaskDraft
        {
            Title = "Buy \"good\" coffee — dark",
            Description = "line 1\nline 2",
            Priority = TaskPriority.High,
            DueAt = due
        });

        Assert.False(File.Exists(OutboxPath + ".tmp"));
        var command = Assert.Single(ReadOutboxCommands());
        Assert.Equal("create", command.GetProperty("op").GetString());
        Assert.Equal(task.ExternalId, command.GetProperty("url").GetString());
        Assert.Equal("Buy \"good\" coffee — dark", command.GetProperty("title").GetString());
        Assert.Equal("line 1\nline 2", command.GetProperty("notes").GetString());
        Assert.Equal("LTFI", command.GetProperty("list").GetString());
        Assert.Equal(due.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture),
            command.GetProperty("dueDate").GetString()); // always full ISO 8601, even at midnight
        Assert.StartsWith("2026-10-10T00:00:00", command.GetProperty("dueDate").GetString());
        Assert.Equal("High", command.GetProperty("priority").GetString());
        Assert.All(command.EnumerateObject(), p => Assert.Equal(JsonValueKind.String, p.Value.ValueKind));

        // Editing a task the iPhone hasn't created yet rewrites the pending create command.
        await tasks.UpdateAsync(task.Id, new TaskDraft { Title = "Buy coffee", Priority = TaskPriority.Medium });
        command = Assert.Single(ReadOutboxCommands());
        Assert.Equal("Buy coffee", command.GetProperty("title").GetString());
        Assert.Equal("", command.GetProperty("dueDate").GetString());
        Assert.Equal("None", command.GetProperty("priority").GetString());
        Assert.Equal(1, await outbox.CountPendingAsync());

        // Deleting it before the iPhone ever saw it withdraws the command.
        await tasks.DeleteAsync(task.Id);
        Assert.Empty(ReadOutboxCommands());
    }

    [Fact]
    public async Task Create_and_complete_are_confirmed_by_later_exports_with_one_evidence_item()
    {
        var outbox = NewOutbox();
        var tasks = NewTasks(outbox);
        var sync = NewSync(outbox);

        var task = await tasks.CreateAsync(new TaskDraft { Title = "Ship LTFI" });
        var url = task.ExternalId!;

        // An export that predates the iPhone applying the create: the task is pending, not removed.
        WriteExport("""{"title":"Something else","list":"TODO GENERAL","creationDate":"2026-10-01T09:00:00Z"}""");
        Assert.Equal(0, (await sync.SyncAsync()).Removed);
        var pending = await tasks.GetByIdAsync(task.Id);
        Assert.Equal(TaskStatus.Ready, pending!.Status);
        Assert.True(pending.IsPendingOnPhone);

        // Complete in LTFI before the phone has even created it: queued after the create.
        await tasks.SetStatusAsync(task.Id, TaskStatus.Completed);
        Assert.Equal(["create", "complete"], ReadOutboxCommands().Select(c => c.GetProperty("op").GetString()));

        // The iPhone ran "LTFI Apply" (created it) but the complete hasn't shown up yet.
        WriteExport($$"""{"title":"Ship LTFI","list":"LTFI","url":"{{url}}","isCompleted":"No","creationDate":"2026-10-04T09:00:00Z"}""");
        var first = await sync.SyncAsync();
        Assert.Equal(1, first.Confirmed);
        Assert.Equal(0, first.Added);
        Assert.Equal(["complete"], ReadOutboxCommands().Select(c => c.GetProperty("op").GetString()));
        var stillPending = await tasks.GetByIdAsync(task.Id);
        Assert.Equal(TaskStatus.Completed, stillPending!.Status);  // the open export doesn't reopen it
        Assert.True(stillPending.IsPendingOnPhone);

        // Now the export shows it completed: everything confirmed, no second evidence item.
        WriteExport($$"""{"title":"Ship LTFI","list":"LTFI","url":"{{url}}","isCompleted":"Yes","completionDate":"2026-10-04T10:00:00Z","creationDate":"2026-10-04T09:00:00Z"}""");
        var second = await sync.SyncAsync();
        Assert.Equal(1, second.Confirmed);
        Assert.Equal(0, second.Completed);
        Assert.Empty(ReadOutboxCommands());
        Assert.Equal(0, await outbox.CountPendingAsync());
        Assert.False((await tasks.GetByIdAsync(task.Id))!.IsPendingOnPhone);

        await using var db = _factory.CreateDbContext();
        Assert.Single(await db.Tasks.Where(t => t.ExternalId == url).ToListAsync());
        var evidence = Assert.Single(await db.Evidence.Where(e => e.Type == EvidenceType.TaskCompleted).ToListAsync());
        Assert.Equal("task", evidence.Source); // LTFI's own completion; the phone's adds nothing
    }

    [Fact]
    public async Task Ltfi_created_reminders_keep_their_project_and_area_whatever_the_list()
    {
        var projects = new ProjectService(_factory);
        var robotics = await projects.CreateAsync(new ProjectDraft { Title = "Robotics" });
        var firmware = await new AreaService(_factory).CreateAsync(robotics.Id, "firmware");
        var tasks = NewTasks();
        var task = await tasks.CreateAsync(new TaskDraft { Title = "Flash", ProjectId = robotics.Id, AreaId = firmware.Id });

        // The phone created it in the LTFI list, then the owner moved it to another list.
        WriteExport($$"""{"title":"Flash","list":"TODO GENERAL","url":"{{task.ExternalId}}","creationDate":"2026-10-04T09:00:00Z"}""");
        await NewSync().SyncAsync();

        var synced = await tasks.GetByIdAsync(task.Id);
        Assert.Equal(robotics.Id, synced!.ProjectId);
        Assert.Equal(firmware.Id, synced.AreaId);
        Assert.Equal("TODO GENERAL", synced.ExternalList);
        Assert.False(synced.IsPendingOnPhone);
    }

    [Fact]
    public async Task Completing_a_reminder_without_an_ltfi_url_is_blocked()
    {
        var outbox = NewOutbox();
        var tasks = NewTasks(outbox);
        WriteExport("""{"title":"Unstamped","list":"TODO GENERAL","creationDate":"2026-10-01T09:00:00Z"}""");
        await NewSync(outbox).SyncAsync();

        Guid id;
        await using (var db = _factory.CreateDbContext()) { id = (await db.Tasks.SingleAsync()).Id; }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tasks.SetStatusAsync(id, TaskStatus.Completed));
        Assert.Contains("complete it on your phone (no LTFI id yet — export with URL stamping first)", ex.Message);
        var current = (await tasks.GetByIdAsync(id))!;
        var viaEditor = await Assert.ThrowsAsync<InvalidOperationException>(() => tasks.UpdateAsync(id,
            new TaskDraft { Title = "Unstamped", ProjectId = current.ProjectId, AreaId = current.AreaId, Status = TaskStatus.Completed }));
        Assert.Equal(ex.Message, viaEditor.Message);

        Assert.NotEqual(TaskStatus.Completed, (await tasks.GetByIdAsync(id))!.Status);
        Assert.Equal(0, await outbox.CountPendingAsync());
        await using var check = _factory.CreateDbContext();
        Assert.Empty(await check.Evidence.ToListAsync());
    }

    [Fact]
    public async Task Focus_gate_still_applies_to_completions_started_in_ltfi()
    {
        var outbox = NewOutbox();
        var tasks = NewTasks(outbox);
        var gated = await tasks.CreateAsync(new TaskDraft { Title = "Study", RequiredMinutes = 30 });

        await Assert.ThrowsAsync<InvalidOperationException>(() => tasks.SetStatusAsync(gated.Id, TaskStatus.Completed));
        Assert.Equal(["create"], ReadOutboxCommands().Select(c => c.GetProperty("op").GetString()));
    }

    [Fact]
    public async Task Missing_icloud_folder_keeps_commands_queued_until_a_flush_can_write()
    {
        var missing = Path.Combine(_folder, "not-yet", "outbox.json");
        var outbox = new ReminderOutbox(_factory, () => missing);
        await NewTasks(outbox).CreateAsync(new TaskDraft { Title = "Later" });

        Assert.False(File.Exists(missing));
        Assert.NotNull(outbox.LastError);
        Assert.Equal(1, await outbox.CountPendingAsync());

        Directory.CreateDirectory(Path.GetDirectoryName(missing)!);
        Assert.True(await outbox.FlushAsync());
        Assert.Null(outbox.LastError);
        Assert.Contains("\"op\": \"create\"", File.ReadAllText(missing));
    }
}
