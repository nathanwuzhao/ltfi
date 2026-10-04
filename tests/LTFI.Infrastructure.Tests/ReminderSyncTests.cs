using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using LTFI.Infrastructure.Reminders;
using LTFI.Infrastructure.Services;
using Xunit;
using TaskStatus = LTFI.Core.Domain.TaskStatus;

namespace LTFI.Infrastructure.Tests;

/// <summary>
/// iCloud Reminders mirror: the tolerant <c>ltfi.reminders/v1</c> parser, and the sync's upsert,
/// idempotent completion evidence, removal marking, and malformed-file handling against a real
/// SQLite database and a real file on disk.
/// </summary>
public class ReminderSyncTests
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "reminders.json");

    private static async Task<string> NewMigratedDbAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ltfi-test-{Guid.NewGuid():N}.db");
        await using var db = new TestDbFactory(path).CreateDbContext();
        await db.Database.MigrateAsync();
        return path;
    }

    private static string NewSnapshotPath() =>
        Path.Combine(Path.GetTempPath(), $"ltfi-reminders-{Guid.NewGuid():N}.json");

    private static void Cleanup(string dbPath, string snapshotPath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { dbPath, dbPath + "-shm", dbPath + "-wal", snapshotPath })
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }

    /// <summary>Writes a v1 export; each item is (title, isCompleted, completionDate?).</summary>
    private static void WriteSnapshot(string path, params (string Title, bool Done, string? DoneAt)[] items)
    {
        var json = string.Join(",", items.Select(i =>
            $$"""{"id":"id-{{i.Title}}","title":"{{i.Title}}","list":"Inbox","isCompleted":{{(i.Done ? "true" : "false")}},"completionDate":"{{i.DoneAt ?? ""}}"}"""));
        File.WriteAllText(path, $$"""{"schema":"ltfi.reminders/v1","exportedAt":"2026-10-03T12:00:00Z","reminders":[{{json}}]}""");
        // Make sure the change token moves even within the file system's timestamp resolution.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 100000)));
    }

    // ---------------------------------------------------------------- parser

    [Fact]
    public void Parser_reads_the_v1_object_shape_with_mixed_case_keys_and_empty_dates()
    {
        var snapshot = ReminderJsonParser.Parse(File.ReadAllText(FixturePath));

        Assert.Equal("shortcuts", snapshot.Producer);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.FromHours(-4)), snapshot.ExportedAt);
        Assert.Equal(3, snapshot.Reminders.Count); // the blank-title item is skipped

        var passport = snapshot.Reminders.Single(r => r.Title == "Renew passport");
        Assert.Equal(TaskPriority.Urgent, passport.Priority);  // High + flagged
        Assert.Equal("Personal", passport.ListName);
        Assert.Null(passport.CompletedAt);                      // "" → null, not "now"
        Assert.Equal(ReminderRules.ComposeKey(passport.CreatedAt), passport.ExternalId);
        Assert.Equal("cd:2026-09-01T12:30:00Z", passport.ExternalId);  // no title: renames are safe
        Assert.Equal(passport.ExternalId, passport.FallbackKey);

        var importer = snapshot.Reminders.Single(r => r.Title == "Write LTFI importer");
        Assert.Equal("LTFI", importer.ListName);                // list given as an object
        Assert.Equal(TaskPriority.Low, importer.Priority);      // Apple 9 = low
        Assert.False(importer.IsCompleted);                     // "No"
        Assert.Equal(new DateTime(2026, 10, 4), importer.DueAt!.Value.LocalDateTime.Date); // date-only = all-day

        var groceries = snapshot.Reminders.Single(r => r.Title == "Buy groceries");
        Assert.Equal("x-apple-reminder://ABC-123", groceries.ExternalId); // real id beats the date key
        Assert.Equal("cd:2026-10-01T13:00:00Z", groceries.FallbackKey);
        Assert.Null(groceries.DueAt);
        Assert.Equal(TaskPriority.Medium, groceries.Priority);  // 0 = none → Medium
        Assert.True(groceries.IsCompleted);
        Assert.NotNull(groceries.CompletedAt);
    }

    [Fact]
    public void Parser_accepts_a_bare_array_and_json_lines_with_identical_keys()
    {
        const string a = """{"title":"One","list":"L","creationDate":"2026-10-01T09:00:00Z"}""";
        const string b = """{"title":"Two","list":"L","isCompleted":true,"completionDate":"2026-10-02T09:00:00Z"}""";

        var fromArray = ReminderJsonParser.Parse($"[{a},{b}]");
        var fromLines = ReminderJsonParser.Parse($"{a}\n{b}\n");
        var fromStringArray = ReminderJsonParser.Parse(
            $$"""{"reminders":[{{System.Text.Json.JsonSerializer.Serialize(a)}},{{System.Text.Json.JsonSerializer.Serialize(b)}}]}""");

        Assert.Equal(2, fromArray.Reminders.Count);
        Assert.Equal(fromArray.Reminders.Select(r => r.ExternalId), fromLines.Reminders.Select(r => r.ExternalId));
        Assert.Equal(fromArray.Reminders.Select(r => r.ExternalId), fromStringArray.Reminders.Select(r => r.ExternalId));
        Assert.True(fromLines.Reminders[1].IsCompleted);
    }

    [Fact]
    public void Key_precedence_is_ltfi_url_then_real_id_then_creation_date()
    {
        const string json = """
            {"reminders":[
              {"title":"Stamped","url":"ltfi://r/20261004090000-12345","id":"apple-1","creationDate":"2026-10-01T09:00:00Z"},
              {"title":"Has id","url":"https://example.com/doc","id":"apple-2","creationDate":"2026-10-01T10:00:00Z"},
              {"title":"Real link","url":"https://example.com/x","creationDate":"2026-10-01T11:00:00Z"},
              {"title":"Plain","creationDate":"2026-10-01T12:00:00-04:00"}
            ]}
            """;

        var r = ReminderJsonParser.Parse(json).Reminders.ToDictionary(x => x.Title);

        Assert.Equal("ltfi://r/20261004090000-12345", r["Stamped"].ExternalId);  // (a) wins over id
        Assert.Equal("cd:2026-10-01T09:00:00Z", r["Stamped"].FallbackKey);       // kept for adoption
        Assert.Equal("apple-2", r["Has id"].ExternalId);                         // (b) a non-ltfi url is ignored
        Assert.Equal("cd:2026-10-01T11:00:00Z", r["Real link"].ExternalId);      // (c)
        Assert.Equal("https://example.com/x", r["Real link"].Url);               // sent as-is, never a key
        Assert.Equal("cd:2026-10-01T16:00:00Z", r["Plain"].ExternalId);          // UTC, to the second
    }

    [Fact]
    public void Title_tiebreak_applies_only_when_creation_dates_collide()
    {
        const string json = """
            [{"title":"  Alpha ","creationDate":"2026-10-01T09:00:00.400Z"},
             {"title":"Beta","creationDate":"2026-10-01T09:00:00.900Z"},
             {"title":"Gamma","creationDate":"2026-10-01T09:00:01Z"}]
            """;

        var keys = ReminderJsonParser.Parse(json).Reminders.Select(x => x.ExternalId).ToList();

        Assert.Equal(["cd:2026-10-01T09:00:00Z|alpha", "cd:2026-10-01T09:00:00Z|beta", "cd:2026-10-01T09:00:01Z"], keys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"reminders\": 42}")]
    [InlineData("{\"title\":\"ok\"}\n{broken")]
    [InlineData("\"just a string\"")]
    public void Parser_rejects_malformed_input(string text)
    {
        Assert.Throws<ReminderSourceException>(() => ReminderJsonParser.Parse(text));
    }

    // ---------------------------------------------------------------- sync

    [Fact]
    public async Task Sync_upserts_the_fixture_and_resync_is_idempotent()
    {
        var dbPath = await NewMigratedDbAsync();
        var snapshotPath = NewSnapshotPath();
        try
        {
            File.Copy(FixturePath, snapshotPath);
            var factory = new TestDbFactory(dbPath);
            var sync = new ReminderSyncService(factory, new FileReminderSource(snapshotPath));

            var first = await sync.SyncAsync();
            Assert.True(first.Succeeded, first.Error);
            Assert.Equal((3, 3, 0, 1, 0), (first.Total, first.Added, first.Updated, first.Completed, first.Removed));

            var second = await sync.SyncAsync();
            Assert.True(second.Succeeded);
            Assert.Equal((0, 0, 0, 0), (second.Added, second.Updated, second.Completed, second.Removed));
            Assert.False(second.HasChanges);

            // Unchanged file → the poll doesn't even read it.
            Assert.Null(await sync.SyncIfChangedAsync());

            await using var db = factory.CreateDbContext();
            Assert.Equal(3, await db.Tasks.CountAsync());
            var evidence = await db.Evidence.ToListAsync();
            var done = Assert.Single(evidence);
            Assert.Equal(EvidenceType.TaskCompleted, done.Type);
            Assert.Equal("icloud-reminders", done.Source);
            Assert.Equal(new DateTimeOffset(2026, 10, 2, 18, 45, 0, TimeSpan.FromHours(-4)), done.OccurredAt);

            var open = await sync.GetOpenAsync();
            Assert.Equal(["Write LTFI importer", "Renew passport"], open.Select(t => t.Title));
            Assert.All(open, t => Assert.True(t.IsExternal));
        }
        finally { Cleanup(dbPath, snapshotPath); }
    }

    [Fact]
    public async Task Completion_on_the_phone_writes_one_evidence_item_and_bypasses_the_focus_gate()
    {
        var dbPath = await NewMigratedDbAsync();
        var snapshotPath = NewSnapshotPath();
        try
        {
            var factory = new TestDbFactory(dbPath);
            var sync = new ReminderSyncService(factory, new FileReminderSource(snapshotPath));

            WriteSnapshot(snapshotPath, ("Ship it", false, null));
            await sync.SyncAsync();

            // A focus requirement LTFI would enforce locally must not block an iPhone completion.
            await using (var db = factory.CreateDbContext())
            {
                var task = await db.Tasks.SingleAsync();
                task.RequiredTime = TimeSpan.FromHours(2);
                await db.SaveChangesAsync();
            }

            WriteSnapshot(snapshotPath, ("Ship it", true, "2026-10-03T09:15:00Z"));
            var completed = await sync.SyncIfChangedAsync();
            Assert.NotNull(completed);
            Assert.Equal(1, completed!.Completed);

            // Same state re-exported, and a fresh service (app restart) — still exactly one.
            WriteSnapshot(snapshotPath, ("Ship it", true, "2026-10-03T09:15:00Z"));
            await sync.SyncIfChangedAsync();
            await new ReminderSyncService(factory, new FileReminderSource(snapshotPath)).SyncAsync();

            await using var check = factory.CreateDbContext();
            var stored = await check.Tasks.SingleAsync();
            Assert.Equal(TaskStatus.Completed, stored.Status);
            Assert.Equal(new DateTimeOffset(2026, 10, 3, 9, 15, 0, TimeSpan.Zero), stored.CompletedAt);
            var evidence = Assert.Single(await check.Evidence.ToListAsync());
            Assert.Equal(stored.Id, evidence.TaskId);
            Assert.Equal(stored.CompletedAt, evidence.OccurredAt);
        }
        finally { Cleanup(dbPath, snapshotPath); }
    }

    [Fact]
    public async Task Reminders_missing_from_the_export_are_marked_removed_not_deleted()
    {
        var dbPath = await NewMigratedDbAsync();
        var snapshotPath = NewSnapshotPath();
        try
        {
            var factory = new TestDbFactory(dbPath);
            var sync = new ReminderSyncService(factory, new FileReminderSource(snapshotPath));

            WriteSnapshot(snapshotPath, ("Keep", false, null), ("Drop", false, null), ("Old done", true, "2026-09-01T00:00:00Z"));
            await sync.SyncAsync();

            // "Drop" deleted on the phone; "Old done" aged out of the 30-day completed window.
            WriteSnapshot(snapshotPath, ("Keep", false, null));
            var result = await sync.SyncAsync();
            Assert.Equal(1, result.Removed);

            await using (var db = factory.CreateDbContext())
            {
                Assert.Equal(3, await db.Tasks.CountAsync()); // nothing hard-deleted
                var dropped = await db.Tasks.SingleAsync(t => t.Title == "Drop");
                Assert.NotNull(dropped.ExternalRemovedAt);
                Assert.Equal(TaskStatus.Canceled, dropped.Status);
                var old = await db.Tasks.SingleAsync(t => t.Title == "Old done");
                Assert.Null(old.ExternalRemovedAt);
                Assert.Equal(TaskStatus.Completed, old.Status);
            }

            Assert.Equal(["Keep"], (await sync.GetOpenAsync()).Select(t => t.Title));

            // An empty export is treated as a glitch and removes nothing.
            File.WriteAllText(snapshotPath, """{"reminders":[]}""");
            Assert.Equal(0, (await sync.SyncAsync()).Removed);

            // It comes back (e.g. undo on the phone): restored to open.
            WriteSnapshot(snapshotPath, ("Keep", false, null), ("Drop", false, null));
            await sync.SyncAsync();
            Assert.Equal(["Drop", "Keep"], (await sync.GetOpenAsync()).Select(t => t.Title).Order());
        }
        finally { Cleanup(dbPath, snapshotPath); }
    }

    [Fact]
    public async Task Malformed_or_missing_file_fails_softly_and_keeps_the_last_good_mirror()
    {
        var dbPath = await NewMigratedDbAsync();
        var snapshotPath = NewSnapshotPath();
        try
        {
            var factory = new TestDbFactory(dbPath);
            var sync = new ReminderSyncService(factory, new FileReminderSource(snapshotPath));

            // Not configured: no file yet.
            Assert.False(sync.GetStatus().IsConfigured);
            Assert.Null(await sync.SyncIfChangedAsync());
            var missing = await sync.SyncAsync();
            Assert.False(missing.Succeeded);
            Assert.Contains(snapshotPath, missing.Error);

            WriteSnapshot(snapshotPath, ("A", false, null), ("B", false, null));
            Assert.True((await sync.SyncAsync()).Succeeded);

            File.WriteAllText(snapshotPath, "{ \"reminders\": [ {\"title\": \"A\"  ");
            File.SetLastWriteTimeUtc(snapshotPath, DateTime.UtcNow.AddDays(1));
            var broken = await sync.SyncIfChangedAsync();
            Assert.NotNull(broken);
            Assert.False(broken!.Succeeded);
            Assert.False(sync.GetStatus().LastResult!.Succeeded);

            // The broken file isn't re-read on every poll, and nothing was removed.
            Assert.Null(await sync.SyncIfChangedAsync());
            Assert.Equal(2, (await sync.GetOpenAsync()).Count);
        }
        finally { Cleanup(dbPath, snapshotPath); }
    }

    [Fact]
    public async Task Unmapped_lists_become_areas_of_the_standing_Life_project()
    {
        var dbPath = await NewMigratedDbAsync();
        var snapshotPath = NewSnapshotPath();
        try
        {
            File.Copy(FixturePath, snapshotPath);
            var factory = new TestDbFactory(dbPath);
            // A project named like a list no longer captures it (the old title-match rule is gone).
            await new ProjectService(factory).CreateAsync(new ProjectDraft { Title = "ltfi" });

            await new ReminderSyncService(factory, new FileReminderSource(snapshotPath)).SyncAsync();

            await using var db = factory.CreateDbContext();
            var life = await db.Projects.Include(p => p.Areas).SingleAsync(p => p.Title == "Life");
            Assert.True(life.IsStanding);
            Assert.Equal(ProjectStatus.Active, life.Status);
            Assert.Equal(["LTFI", "Personal"], life.Areas.Select(a => a.Name).Order());

            var importer = await db.Tasks.SingleAsync(t => t.Title == "Write LTFI importer");
            Assert.Equal(life.Id, importer.ProjectId);
            Assert.Equal(life.Areas.Single(a => a.Name == "LTFI").Id, importer.AreaId);
            Assert.All(await db.Tasks.ToListAsync(), t => Assert.Equal(life.Id, t.ProjectId));

            // The standing project doesn't use up the active-project limit.
            Assert.Equal(1, await new ProjectService(factory).CountActiveAsync());
        }
        finally { Cleanup(dbPath, snapshotPath); }
    }

    [Fact]
    public async Task List_map_overrides_the_default_and_a_missing_mapped_project_falls_back()
    {
        var dbPath = await NewMigratedDbAsync();
        var snapshotPath = NewSnapshotPath();
        try
        {
            var factory = new TestDbFactory(dbPath);
            var school = await new ProjectService(factory).CreateAsync(new ProjectDraft { Title = "School" });
            var settings = new LTFI.Infrastructure.Settings.RemindersSettings
            {
                StandingProject = "Life",
                ListMap = new()
                {
                    ["gatech"] = new() { Project = "school", Area = "Classes" },
                    ["HOMEWORK"] = new() { Project = "School" },
                    ["job"] = new() { Project = "Does Not Exist", Area = "x" }
                }
            };

            File.WriteAllText(snapshotPath, """
                {"reminders":[
                  {"title":"Read ch 3","list":"GATECH","creationDate":"2026-10-01T09:00:00Z"},
                  {"title":"PSET 4","list":"HOMEWORK","creationDate":"2026-10-01T10:00:00Z"},
                  {"title":"Apply","list":"job","creationDate":"2026-10-01T11:00:00Z"},
                  {"title":"Milk","list":"TODO GENERAL","creationDate":"2026-10-01T12:00:00Z"}
                ]}
                """);

            await new ReminderSyncService(factory, new FileReminderSource(snapshotPath), settings).SyncAsync();

            await using var db = factory.CreateDbContext();
            var areas = await db.Areas.ToListAsync();
            var life = await db.Projects.SingleAsync(p => p.Title == "Life");
            var t = await db.Tasks.ToDictionaryAsync(x => x.Title);

            Assert.Equal(school.Id, t["Read ch 3"].ProjectId);
            Assert.Equal("Classes", areas.Single(a => a.Id == t["Read ch 3"].AreaId).Name);
            Assert.Equal(school.Id, t["PSET 4"].ProjectId);
            Assert.Null(t["PSET 4"].AreaId);                         // mapped with no area
            Assert.Equal(life.Id, t["Apply"].ProjectId);              // unknown project → default rule
            Assert.Equal("job", areas.Single(a => a.Id == t["Apply"].AreaId).Name);
            Assert.Equal("TODO GENERAL", areas.Single(a => a.Id == t["Milk"].AreaId).Name);
            Assert.DoesNotContain(await db.Projects.ToListAsync(), p => p.Title == "Does Not Exist");
        }
        finally { Cleanup(dbPath, snapshotPath); }
    }

    [Fact]
    public async Task Renaming_or_moving_a_reminder_on_the_phone_keeps_its_task()
    {
        var dbPath = await NewMigratedDbAsync();
        var snapshotPath = NewSnapshotPath();
        try
        {
            var factory = new TestDbFactory(dbPath);
            var sync = new ReminderSyncService(factory, new FileReminderSource(snapshotPath));

            File.WriteAllText(snapshotPath, """[{"title":"Draft","list":"GATECH","creationDate":"2026-10-01T09:00:00Z"}]""");
            await sync.SyncAsync();
            Guid id;
            await using (var db = factory.CreateDbContext()) { id = (await db.Tasks.SingleAsync()).Id; }

            File.WriteAllText(snapshotPath, """[{"title":"Final draft","list":"HOMEWORK","creationDate":"2026-10-01T09:00:00Z"}]""");
            File.SetLastWriteTimeUtc(snapshotPath, DateTime.UtcNow.AddMinutes(5));
            var result = await sync.SyncAsync();

            Assert.Equal((0, 1, 0), (result.Added, result.Updated, result.Removed));
            await using var check = factory.CreateDbContext();
            var task = await check.Tasks.Include(x => x.Area).SingleAsync();
            Assert.Equal(id, task.Id);
            Assert.Equal("Final draft", task.Title);
            Assert.Equal("HOMEWORK", task.Area!.Name);
        }
        finally { Cleanup(dbPath, snapshotPath); }
    }

    [Fact]
    public async Task A_freshly_url_stamped_reminder_adopts_its_creation_date_row()
    {
        var dbPath = await NewMigratedDbAsync();
        var snapshotPath = NewSnapshotPath();
        try
        {
            var factory = new TestDbFactory(dbPath);
            var sync = new ReminderSyncService(factory, new FileReminderSource(snapshotPath));

            // Before URL stamping: keyed by creation date, completed on the phone (one evidence item).
            File.WriteAllText(snapshotPath, """
                [{"title":"Pay rent","list":"TODO GENERAL","creationDate":"2026-10-01T09:00:00Z",
                  "isCompleted":"Yes","completionDate":"2026-10-02T09:00:00Z"}]
                """);
            await sync.SyncAsync();
            Guid id;
            await using (var db = factory.CreateDbContext())
            {
                var before = await db.Tasks.SingleAsync();
                Assert.Equal("cd:2026-10-01T09:00:00Z", before.ExternalId);
                id = before.Id;
            }

            // The export Shortcut now stamps a URL on the same reminder.
            File.WriteAllText(snapshotPath, """
                [{"title":"Pay rent","list":"TODO GENERAL","creationDate":"2026-10-01T09:00:00Z",
                  "url":"ltfi://r/20261001090000-54321","isCompleted":"Yes","completionDate":"2026-10-02T09:00:00Z"}]
                """);
            File.SetLastWriteTimeUtc(snapshotPath, DateTime.UtcNow.AddMinutes(5));
            var result = await sync.SyncAsync();

            Assert.Equal((0, 1, 0, 0), (result.Added, result.Updated, result.Completed, result.Removed));
            await using var check = factory.CreateDbContext();
            var task = await check.Tasks.SingleAsync();                 // no duplicate
            Assert.Equal(id, task.Id);                                   // history kept
            Assert.Equal("ltfi://r/20261001090000-54321", task.ExternalId);
            Assert.Single(await check.Evidence.ToListAsync());          // no duplicate completion evidence
        }
        finally { Cleanup(dbPath, snapshotPath); }
    }
}
