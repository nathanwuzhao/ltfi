using Microsoft.EntityFrameworkCore;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using LTFI.Infrastructure.Persistence;
using LTFI.Infrastructure.Settings;
using TaskStatus = LTFI.Core.Domain.TaskStatus;

namespace LTFI.Infrastructure.Services;

/// <summary>
/// Mirrors reminders from an <see cref="IReminderSource"/> onto <see cref="TaskItem"/> rows
/// (<see cref="TaskItem.ExternalSource"/> = <see cref="ReminderRules.SourceKey"/>). iCloud
/// Reminders is authoritative for title/notes/due/priority/completion and — via its list — for the
/// project/area (list → <see cref="RemindersSettings.ListMap"/>, else standing project + area named
/// after the list). Reminders LTFI created itself keep the project/area LTFI recorded.
/// InProgress/Deferred and focus sessions are preserved. Each pass also confirms outbox commands the
/// export shows as done. Each pass is a single SaveChanges (one transaction), so a bad snapshot never
/// half-applies.
/// </summary>
public sealed class ReminderSyncService(
    IDbContextFactory<LtfiDbContext> contextFactory,
    IReminderSource source,
    RemindersSettings? settings = null,
    IReminderOutbox? outbox = null) : IReminderSyncService
{
    private const string Key = ReminderRules.SourceKey;

    private readonly IDbContextFactory<LtfiDbContext> _contextFactory = contextFactory;
    private readonly IReminderSource _source = source;
    private readonly RemindersSettings _settings = settings ?? new RemindersSettings();
    private readonly IReminderOutbox? _outbox = outbox;

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

        var result = await ApplyAsync(snapshot, cancellationToken);
        if (_outbox is not null)
        {
            // Confirmed commands drop out of outbox.json (also rewrites it if it went missing).
            await _outbox.FlushAsync(cancellationToken);
        }

        return Remember(result, probe.Version);
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

        // Projects (with areas) for list → project/area placement; the standing project and areas
        // are created on demand.
        var placement = new Placement(
            db,
            await db.Projects.Include(p => p.Areas).ToListAsync(cancellationToken),
            _settings,
            now);

        // Write-back commands the iPhone hasn't confirmed yet.
        var unconfirmed = await db.Outbox
            .Where(c => c.ConfirmedAt == null)
            .ToListAsync(cancellationToken);
        var pendingCreates = unconfirmed
            .Where(c => c.Op == OutboxCommand.CreateOp)
            .Select(c => c.ExternalUrl)
            .ToHashSet(StringComparer.Ordinal);

        // Tasks that already earned their reminder completion evidence (dedupe across re-syncs).
        var evidenced = (await db.Evidence.AsNoTracking()
                .Where(e => e.Source == Key && e.TaskId != null)
                .Select(e => new { e.TaskId, e.Type })
                .ToListAsync(cancellationToken))
            .Where(e => e.Type == EvidenceType.TaskCompleted)
            .Select(e => e.TaskId!.Value)
            .ToHashSet();

        int added = 0, updated = 0, completed = 0, removed = 0, confirmed = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var reminder in incoming)
        {
            seen.Add(reminder.ExternalId);
            existing.TryGetValue(reminder.ExternalId, out var task);
            var changed = false;

            // Adoption: the export Shortcut just stamped an ltfi:// URL on a reminder LTFI already
            // knows under its creation-date key. Re-key that row instead of adding a duplicate, so its
            // history (focus time, completion evidence) carries over.
            if (task is null && ReminderRules.IsLtfiUrl(reminder.ExternalId)
                && FindAdoptable(existing, reminder) is { } adopted)
            {
                existing.Remove(adopted.ExternalId!);
                adopted.ExternalId = reminder.ExternalId;
                existing[reminder.ExternalId] = adopted;
                task = adopted;
                changed = true;
            }

            var isNew = task is null;
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
                existing[reminder.ExternalId] = task;
                added++;
            }

            changed |= ApplyFields(task, reminder);

            // Project/area follow the list — except for a reminder LTFI created, which keeps the
            // project/area LTFI recorded for it (when it has a row to remember them by).
            if (isNew || !ReminderRules.IsLtfiCreatedUrl(task.ExternalId))
            {
                var (projectId, areaId) = placement.Resolve(reminder.ListName);
                if (task.ProjectId != projectId || task.AreaId != areaId)
                {
                    task.ProjectId = projectId;
                    task.AreaId = areaId;
                    changed = true;
                }
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
            // completed (its "complete" command may simply not have been applied yet), rather than
            // flip-flopping on every sync. A task already completed in LTFI gets no second evidence.
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
        // A task LTFI created that the iPhone hasn't made yet is pending, not removed.
        if (incoming.Count > 0)
        {
            foreach (var task in existing.Values)
            {
                if (seen.Contains(task.ExternalId!) || task.ExternalRemovedAt is not null
                    || task.Status is TaskStatus.Completed or TaskStatus.Canceled
                    || pendingCreates.Contains(task.ExternalId!))
                {
                    continue;
                }

                task.ExternalRemovedAt = now;
                task.Status = TaskStatus.Canceled;
                task.UpdatedAt = now;
                removed++;
            }
        }

        // Confirm write-back commands the export shows as done: create → a reminder with that url
        // exists; complete → that url is completed. Confirmed commands leave outbox.json.
        var byId = incoming.ToDictionary(r => r.ExternalId, StringComparer.Ordinal);
        foreach (var command in unconfirmed)
        {
            var done = byId.TryGetValue(command.ExternalUrl, out var reminder) && command.Op switch
            {
                OutboxCommand.CreateOp => true,
                OutboxCommand.CompleteOp => reminder!.IsCompleted,
                _ => false
            };

            if (done)
            {
                command.ConfirmedAt = now;
                confirmed++;
            }
        }

        // Tasks whose commands are all confirmed are no longer pending on the iPhone.
        var stillPending = unconfirmed
            .Where(c => c.ConfirmedAt is null)
            .Select(c => c.ExternalUrl)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var task in existing.Values)
        {
            if (task.ExternalPendingSince is not null && !stillPending.Contains(task.ExternalId!))
            {
                task.ExternalPendingSince = null;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return new ReminderSyncResult(
            true, now, snapshot.ExportedAt, incoming.Count, added, updated, completed, removed,
            Confirmed: confirmed);
    }

    /// <summary>
    /// The row a freshly URL-stamped reminder was known by before: its creation-date key (with or
    /// without the title tiebreak). Only rows not already on an ltfi:// id qualify.
    /// </summary>
    private static TaskItem? FindAdoptable(Dictionary<string, TaskItem> existing, ExternalReminder reminder)
    {
        var candidates = new[]
        {
            reminder.FallbackKey,
            ReminderRules.ComposeKey(reminder.CreatedAt),
            ReminderRules.ComposeKey(reminder.CreatedAt, reminder.Title)
        };

        foreach (var key in candidates.Where(k => k is not null).Distinct(StringComparer.Ordinal))
        {
            if (existing.TryGetValue(key!, out var task) && !ReminderRules.IsLtfiUrl(task.ExternalId))
            {
                return task;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a reminder list to (project, area): <see cref="RemindersSettings.ListMap"/> first,
    /// else the standing project with an area named after the list. The standing project and any
    /// missing area are created on demand (tracked in the caller's context, saved with the pass).
    /// A mapped project that doesn't exist falls back to the default rule.
    /// </summary>
    private sealed class Placement(LtfiDbContext db, List<Project> projects, RemindersSettings settings, DateTimeOffset now)
    {
        private readonly string _standing = string.IsNullOrWhiteSpace(settings.StandingProject)
            ? "Life"
            : settings.StandingProject.Trim();

        private readonly Dictionary<string, ReminderListMapping> _map =
            new(settings.ListMap ?? new Dictionary<string, ReminderListMapping>(), StringComparer.OrdinalIgnoreCase);

        public (Guid ProjectId, Guid? AreaId) Resolve(string? listName)
        {
            var list = string.IsNullOrWhiteSpace(listName) ? null : listName.Trim();

            Project? project = null;
            string? areaName = list;
            if (list is not null && _map.TryGetValue(list, out var mapping) && !string.IsNullOrWhiteSpace(mapping.Project))
            {
                project = FindProject(mapping.Project.Trim());
                if (project is not null)
                {
                    areaName = string.IsNullOrWhiteSpace(mapping.Area) ? null : mapping.Area.Trim();
                }
            }

            project ??= EnsureStandingProject();
            return (project.Id, areaName is null ? null : EnsureArea(project, areaName).Id);
        }

        private Project? FindProject(string title) =>
            projects
                .Where(p => string.Equals(p.Title.Trim(), title, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.IsArchived) // prefer a live project over an archived namesake
                .FirstOrDefault();

        private Project EnsureStandingProject()
        {
            var project = FindProject(_standing);
            if (project is null)
            {
                project = new Project
                {
                    Title = _standing,
                    Description = "Standing project for everyday reminders (created by the iCloud Reminders sync).",
                    Status = ProjectStatus.Active,
                    IsStanding = true,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.Projects.Add(project);
                projects.Add(project);
            }
            else if (!project.IsStanding)
            {
                // The configured standing project is exempt from the limit by definition.
                project.IsStanding = true;
                project.UpdatedAt = now;
            }

            return project;
        }

        private ProjectArea EnsureArea(Project project, string name)
        {
            var area = project.Areas.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (area is null)
            {
                area = new ProjectArea
                {
                    ProjectId = project.Id,
                    Name = name,
                    SortOrder = project.Areas.Count == 0 ? 0 : project.Areas.Max(a => a.SortOrder) + 1,
                    CreatedAt = now
                };
                db.Areas.Add(area);
                project.Areas.Add(area);
            }

            return area;
        }
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
