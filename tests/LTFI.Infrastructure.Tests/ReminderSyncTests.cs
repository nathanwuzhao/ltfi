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
        Assert.Equal(ReminderRules.ComposeKey("Personal", passport.CreatedAt, "Renew passport"), passport.ExternalId);
        Assert.StartsWith("sc:personal|2026-09-01T12:30:00Z|", passport.ExternalId);

        var importer = snapshot.Reminders.Single(r => r.Title == "Write LTFI importer");
        Assert.Equal("LTFI", importer.ListName);                // list given as an object
        Assert.Equal(TaskPriority.Low, importer.Priority);      // Apple 9 = low
        Assert.False(importer.IsCompleted);                     // "No"
        Assert.Equal(new DateTime(2026, 10, 4), importer.DueAt!.Value.LocalDateTime.Date); // date-only = all-day

        var groceries = snapshot.Reminders.Single(r => r.Title == "Buy groceries");
        Assert.Equal("x-apple-reminder://ABC-123", groceries.ExternalId); // real id wins
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
    public async Task Reminder_lists_map_onto_projects_by_name()
    {
        var dbPath = await NewMigratedDbAsync();
        var snapshotPath = NewSnapshotPath();
        try
        {
            File.Copy(FixturePath, snapshotPath);
            var factory = new TestDbFactory(dbPath);
            var project = await new ProjectService(factory).CreateAsync(new ProjectDraft { Title = "ltfi" });

            await new ReminderSyncService(factory, new FileReminderSource(snapshotPath)).SyncAsync();

            await using var db = factory.CreateDbContext();
            var importer = await db.Tasks.SingleAsync(t => t.Title == "Write LTFI importer");
            Assert.Equal(project.Id, importer.ProjectId);
            Assert.Equal("LTFI", importer.ExternalList);
            Assert.Null((await db.Tasks.SingleAsync(t => t.Title == "Renew passport")).ProjectId);
        }
        finally { Cleanup(dbPath, snapshotPath); }
    }
}
