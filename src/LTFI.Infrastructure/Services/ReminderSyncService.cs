using Microsoft.EntityFrameworkCore;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using LTFI.Infrastructure.Persistence;
using TaskStatus = LTFI.Core.Domain.TaskStatus;

namespace LTFI.Infrastructure.Services;

/// <summary>
/// Mirrors reminders from an <see cref="IReminderSource"/> onto <see cref="TaskItem"/> rows
/// (<see cref="TaskItem.ExternalSource"/> = <see cref="ReminderRules.SourceKey"/>). One-way:
/// iCloud Reminders is authoritative for title/notes/due/priority/completion; LTFI-only state
/// (project link, InProgress/Deferred, focus sessions) is preserved. Each pass is a single
/// SaveChanges (one transaction), so a bad snapshot never half-applies.
/// </summary>
public sealed class ReminderSyncService(
    IDbContextFactory<LtfiDbContext> contextFactory,
    IReminderSource source) : IReminderSyncService
{
    private const string Key = ReminderRules.SourceKey;

    private readonly IDbContextFactory<LtfiDbContext> _contextFactory = contextFactory;
    private readonly IReminderSource _source = source;

    // Startup, the shell's 15s poll, and the SYNC button can overlap; serialise passes.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _lastVersion;
    private ReminderSyncResult? _lastResult;

    public ReminderSyncStatus GetStatus()
    {
        var probe = _source.Probe();
        return new ReminderSyncStatus(probe.IsAvailable, probe.Location, _lastResult);
    }

    public async Task<ReminderSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await SyncCoreAsync(_source.Probe(), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ReminderSyncResult?> SyncIfChangedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var probe = _source.Probe();
            if (!probe.IsAvailable || probe.Version == _lastVersion)
            {
                return null;
            }

            return await SyncCoreAsync(probe, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TaskItem>> GetOpenAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var mirrored = await db.Tasks.AsNoTracking()
            .Where(t => t.ExternalSource == Key && t.ExternalRemovedAt == null)
            .ToListAsync(cancellationToken);

        // Status/due ordering in memory (string enums + DateTimeOffset, see the other services).
        return mirrored
            .Where(t => t.Status is not (TaskStatus.Completed or TaskStatus.Canceled))
            .OrderBy(t => t.ExternalList ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.DueAt is null)
            .ThenBy(t => t.DueAt)
            .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<ReminderSyncResult> SyncCoreAsync(ReminderSourceProbe probe, CancellationToken cancellationToken)
    {
        if (!probe.IsAvailable)
        {
            return Remember(ReminderSyncResult.Failed($"No reminders file at {probe.Location}."), probe.Version);
        }

        ReminderSnapshot snapshot;
        try
        {
            snapshot = await _source.ReadAsync(cancellationToken);
        }
        catch (ReminderSourceException ex)
        {
            // Keep the last good mirror. Remember the version so the poll doesn't re-read a broken
            // file every 15s; the next export (or a manual SYNC) tries again.
            return Remember(ReminderSyncResult.Failed(ex.Message), probe.Version);
        }

        return Remember(await ApplyAsync(snapshot, cancellationToken), probe.Version);
    }

    private ReminderSyncResult Remember(ReminderSyncResult result, string? version)
    {
        _lastVersion = version;
        _lastResult = result;
        return result;
    }

    /// <summary>Upserts a snapshot onto the mirrored task rows.</summary>
    private async Task<ReminderSyncResult> ApplyAsync(ReminderSnapshot snapshot, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;

        // Duplicate keys in one export (e.g. identical title/list/creation): last one wins.
        var incoming = snapshot.Reminders
            .GroupBy(r => r.ExternalId, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToList();

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var existing = (await db.Tasks
                .Where(t => t.ExternalSource == Key)
                .ToListAsync(cancellationToken))
            .ToDictionary(t => t.ExternalId!, StringComparer.Ordinal);

        // List → project by (case-insensitive) title, so a "LTFI" list lands under the "LTFI" project.
        var projectsByTitle = (await db.Projects.AsNoTracking()
                .Select(p => new { p.Id, p.Title })
                .ToListAsync(cancellationToken))
            .GroupBy(p => p.Title.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        // Tasks that already earned their reminder completion evidence (dedupe across re-syncs).
        var evidenced = (await db.Evidence.AsNoTracking()
                .Where(e => e.Source == Key && e.TaskId != null)
                .Select(e => new { e.TaskId, e.Type })
                .ToListAsync(cancellationToken))
            .Where(e => e.Type == EvidenceType.TaskCompleted)
            .Select(e => e.TaskId!.Value)
            .ToHashSet();

        int added = 0, updated = 0, completed = 0, removed = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var reminder in incoming)
        {
            seen.Add(reminder.ExternalId);
            var isNew = !existing.TryGetValue(reminder.ExternalId, out var task);
            if (task is null)
            {
                task = new TaskItem
                {
                    ExternalSource = Key,
                    ExternalId = reminder.ExternalId,
                    Status = TaskStatus.Ready,
                    CreatedAt = reminder.CreatedAt ?? now,
                    UpdatedAt = now
                };
                db.Tasks.Add(task);
                added++;
            }

            var changed = ApplyFields(task, reminder);

            if (task.ProjectId is null && reminder.ListName is { } list
                && projectsByTitle.TryGetValue(list.Trim(), out var projectId))
            {
                task.ProjectId = projectId;
                changed = true;
            }

            // Back in the export after being marked removed: restore it.
            if (task.ExternalRemovedAt is not null)
            {
                task.ExternalRemovedAt = null;
                if (task.Status == TaskStatus.Canceled)
                {
                    task.Status = TaskStatus.Ready;
                }
                changed = true;
            }

            // An external completion is authoritative: it bypasses the RequiredTime focus gate.
            // The reverse (reopened on the phone) is not mirrored — a task completed in LTFI stays
            // completed until write-back exists, rather than flip-flopping on every sync.
            if (reminder.IsCompleted && task.Status != TaskStatus.Completed)
            {
                var completedAt = reminder.CompletedAt ?? snapshot.ExportedAt ?? now;
                task.Status = TaskStatus.Completed;
                task.CompletedAt = completedAt;
                changed = true;
                completed++;

                if (evidenced.Add(task.Id))
                {
                    db.Evidence.Add(new EvidenceItem
                    {
                        Type = EvidenceType.TaskCompleted,
                        Source = Key,
                        Title = task.Title,
                        Summary = reminder.ListName is { } l ? $"Completed on iPhone · {l}" : "Completed on iPhone",
                        ProjectId = task.ProjectId,
                        TaskId = task.Id,
                        OccurredAt = completedAt
                    });
                }
            }

            if (changed)
            {
                task.UpdatedAt = now;
                if (!isNew)
                {
                    updated++;
                }
            }
        }

        // Open reminders missing from the export were deleted (or moved/renamed) on the phone.
        // Completed ones simply aged out of the export's 30-day window, so leave those alone.
        // An empty export is treated as suspect (a glitched Shortcut run) and removes nothing.
        if (incoming.Count > 0)
        {
            foreach (var task in existing.Values)
            {
                if (seen.Contains(task.ExternalId!) || task.ExternalRemovedAt is not null
                    || task.Status is TaskStatus.Completed or TaskStatus.Canceled)
                {
                    continue;
                }

                task.ExternalRemovedAt = now;
                task.Status = TaskStatus.Canceled;
                task.UpdatedAt = now;
                removed++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return new ReminderSyncResult(
            true, now, snapshot.ExportedAt, incoming.Count, added, updated, completed, removed);
    }

    /// <summary>Copies the source-owned fields; returns true if anything differed.</summary>
    private static bool ApplyFields(TaskItem task, ExternalReminder reminder)
    {
        var notes = string.IsNullOrWhiteSpace(reminder.Notes) ? null : reminder.Notes.Trim();
        var list = string.IsNullOrWhiteSpace(reminder.ListName) ? null : reminder.ListName.Trim();

        var changed = task.Title != reminder.Title
                      || task.Description != notes
                      || task.DueAt != reminder.DueAt
                      || task.Priority != reminder.Priority
                      || task.ExternalList != list;

        task.Title = reminder.Title;
        task.Description = notes;
        task.DueAt = reminder.DueAt;
        task.Priority = reminder.Priority;
        task.ExternalList = list;
        return changed;
    }
}
