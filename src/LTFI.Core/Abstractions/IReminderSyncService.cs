using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LTFI.Core.Domain;

namespace LTFI.Core.Abstractions;

/// <summary>Outcome of one sync pass. On failure the counts are zero and no data was changed.</summary>
public sealed record ReminderSyncResult(
    bool Succeeded,
    DateTimeOffset SyncedAt,
    DateTimeOffset? ExportedAt = null,
    int Total = 0,
    int Added = 0,
    int Updated = 0,
    int Completed = 0,
    int Removed = 0,
    string? Error = null,
    int Confirmed = 0)
{
    /// <summary>True when local data changed (including write-back commands the export confirmed).</summary>
    public bool HasChanges => Added + Updated + Completed + Removed + Confirmed > 0;

    public static ReminderSyncResult Failed(string error) => new(false, DateTimeOffset.Now, Error: error);
}

/// <summary>What the UI needs to render the sync state (configured? where? last outcome?).</summary>
public sealed record ReminderSyncStatus(bool IsConfigured, string Location, ReminderSyncResult? LastResult);

/// <summary>
/// Mirror of external reminders onto LTFI tasks (iCloud Reminders is the source of truth).
/// Upserts by external id, files each reminder under a project/area from its list, records one
/// TaskCompleted evidence item per completion (so phone completions feed points and the activity
/// graph), marks vanished reminders as removed, and confirms LTFI's write-back commands.
/// </summary>
public interface IReminderSyncService
{
    ReminderSyncStatus GetStatus();

    /// <summary>Reads the source and applies it. Never throws for source problems — inspect the result.</summary>
    Task<ReminderSyncResult> SyncAsync(CancellationToken cancellationToken = default);

    /// <summary>Like <see cref="SyncAsync"/>, but returns null without reading when the source's
    /// change token matches the last attempt (or the source isn't available).</summary>
    Task<ReminderSyncResult?> SyncIfChangedAsync(CancellationToken cancellationToken = default);

    /// <summary>Open (not completed, canceled or removed) mirrored reminders, ordered by list, due date, title.</summary>
    Task<IReadOnlyList<TaskItem>> GetOpenAsync(CancellationToken cancellationToken = default);
}
