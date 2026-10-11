using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using LTFI.Services.Audio;
using Serilog;

namespace LTFI.ViewModels;

/// <summary>One open reminder row: title, list-relative due text, and its slack colour.</summary>
public sealed record ReminderRow(string Title, string DueText, IBrush DueBrush, string PriorityText, bool IsPending = false);

/// <summary>Open reminders under one Reminders list.</summary>
public sealed record ReminderGroup(string ListName, int Count, IReadOnlyList<ReminderRow> Items);

/// <summary>
/// The REMINDERS panel (Today page): open iCloud Reminders grouped by list, a SYNC button, the
/// last-sync readout with a staleness warning, and a "not configured" state that says where the
/// iPhone Shortcut's export should land. The shell also drives <see cref="SyncIfChangedAsync"/>
/// on start-up and on its 15s poll.
/// </summary>
public partial class RemindersPanelViewModel : ViewModelBase
{
    /// <summary>An export older than this gets a stale warning (the Shortcut runs several times a day).</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    private readonly IReminderSyncService _sync;
    private readonly IReminderOutbox _outbox;
    private readonly NotificationSounds _sounds;

    /// <summary>Background sync calls so far; the first (app start-up) never pings.</summary>
    private int _backgroundSyncs;

    /// <summary>Raised after a sync that changed local data, so the shell can refresh visible pages.</summary>
    public event EventHandler? Synced;

    public ObservableCollection<ReminderGroup> Groups { get; } = [];

    [ObservableProperty] private bool isConfigured;
    [ObservableProperty] private bool hasReminders;
    [ObservableProperty] private bool isStale;
    [ObservableProperty] private bool isSyncing;
    [ObservableProperty] private string location = string.Empty;
    [ObservableProperty] private string statusText = "NOT SYNCED";
    [ObservableProperty] private string countText = "0 OPEN";
    [ObservableProperty] private string errorText = string.Empty;

    /// <summary>Write-back commands (create/complete) waiting for the iPhone's LTFI Apply Shortcut.</summary>
    [ObservableProperty] private int outboxCount;
    [ObservableProperty] private string outboxText = string.Empty;
    [ObservableProperty] private string outboxError = string.Empty;

    /// <summary>Shell header readout: "SYNCED 14:32" from the last good sync (manual or background).</summary>
    [ObservableProperty] private string headerSyncText = "NOT SYNCED";
    [ObservableProperty] private string headerSyncTip = "No iPhone reminders sync yet.";

    /// <summary>True when the latest attempt failed or the export is stale (header readout turns amber).</summary>
    [ObservableProperty] private bool hasSyncProblem;

    private ReminderSyncResult? _lastGoodResult;
    private ReminderSyncResult? _lastAttempt;
    private DateTimeOffset? _lastCheckedAt;

    public RemindersPanelViewModel(IReminderSyncService sync, IReminderOutbox outbox, NotificationSounds sounds)
    {
        _sync = sync;
        _outbox = outbox;
        _sounds = sounds;
    }

    /// <summary>Re-reads status + open reminders from the DB (no file read).</summary>
    public async Task RefreshAsync()
    {
        var status = _sync.GetStatus();
        IsConfigured = status.IsConfigured;
        Location = status.Location;
        ApplyResult(status.LastResult);

        var open = await _sync.GetOpenAsync();
        var today = DateTime.Today;

        Groups.Clear();
        foreach (var group in open.GroupBy(t => t.ExternalList ?? "Reminders"))
        {
            Groups.Add(new ReminderGroup(
                group.Key.ToUpperInvariant(),
                group.Count(),
                group.Select(t => ToRow(t, today)).ToList()));
        }

        HasReminders = open.Count > 0;
        CountText = $"{open.Count} OPEN";

        OutboxCount = await _outbox.CountPendingAsync();
        OutboxText = OutboxCount == 0
            ? "OUTBOX EMPTY"
            : $"OUTBOX {OutboxCount} → iPHONE (waiting for LTFI Apply)";
        OutboxError = OutboxCount > 0 ? _outbox.LastError ?? string.Empty : string.Empty;
    }

    /// <summary>Called by the shell on start-up and every ~15s; only reads the file when it changed.</summary>
    public async Task SyncIfChangedAsync()
    {
        var isStartup = Interlocked.Increment(ref _backgroundSyncs) == 1;
        try
        {
            var result = await _sync.SyncIfChangedAsync();
            _lastCheckedAt = DateTimeOffset.Now;
            if (result is not null)
            {
                await AfterSyncAsync(result, allowPing: !isStartup);
            }
            else
            {
                // Nothing new: re-read the status (cheap, no file read) so the header's readout,
                // staleness and "last checked" line stay current even if Today was never opened.
                var status = _sync.GetStatus();
                IsConfigured = status.IsConfigured;
                Location = status.Location;
                ApplyResult(status.LastResult);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Background reminders sync failed");
        }
    }

    [RelayCommand]
    private async Task SyncAsync()
    {
        if (IsSyncing)
        {
            return;
        }

        IsSyncing = true;
        try
        {
            // Re-write outbox.json too (e.g. the iCloud folder appeared since the last attempt).
            await _outbox.FlushAsync();
            var result = await _sync.SyncAsync();
            _lastCheckedAt = DateTimeOffset.Now;
            await AfterSyncAsync(result, allowPing: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reminders sync failed");
            ErrorText = ex.Message;
            _lastAttempt = ReminderSyncResult.Failed(ex.Message);
            UpdateHeader();
        }
        finally
        {
            IsSyncing = false;
        }
    }

    /// <summary>
    /// True when the sync brought something from the iPhone worth a ping: new reminders,
    /// completions, or LTFI write-back commands the export confirmed. Edits/removals stay quiet.
    /// </summary>
    internal static bool IsPingWorthy(ReminderSyncResult result) =>
        result.Succeeded && result.Added + result.Completed + result.Confirmed > 0;

    private async Task AfterSyncAsync(ReminderSyncResult result, bool allowPing)
    {
        if (allowPing && IsPingWorthy(result))
        {
            _sounds.PlaySyncPing();
        }

        if (result.Succeeded)
        {
            Log.Information(
                "Reminders synced: {Total} total, +{Added} ~{Updated} done {Completed} removed {Removed}",
                result.Total, result.Added, result.Updated, result.Completed, result.Removed);
        }
        else
        {
            Log.Warning("Reminders sync failed: {Error}", result.Error);
        }

        await RefreshAsync();
        if (result.HasChanges)
        {
            Synced?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplyResult(ReminderSyncResult? result)
    {
        if (result is not null)
        {
            _lastAttempt = result;
            if (result.Succeeded)
            {
                _lastGoodResult = result;
            }
        }

        try
        {
            ApplyPanelStatus(result);
        }
        finally
        {
            UpdateHeader();
        }
    }

    private void ApplyPanelStatus(ReminderSyncResult? result)
    {
        if (result is null)
        {
            StatusText = "NOT SYNCED";
            ErrorText = string.Empty;
            IsStale = false;
            return;
        }

        // Failures keep showing the last good mirror; the error explains why it didn't update.
        ErrorText = result.Succeeded || !IsConfigured ? string.Empty : result.Error ?? "Sync failed.";
        if (!result.Succeeded)
        {
            return;
        }

        var synced = result.SyncedAt.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (result.ExportedAt is { } exported)
        {
            StatusText = $"SYNCED {synced} · IPHONE EXPORT {Ago(DateTimeOffset.Now - exported)} · {result.Total} ITEMS";
            IsStale = DateTimeOffset.Now - exported > StaleAfter;
        }
        else
        {
            StatusText = $"SYNCED {synced} · {result.Total} ITEMS";
            IsStale = false;
        }
    }

    /// <summary>
    /// The shell header's "SYNCED HH:mm" readout and its tooltip: the last good sync's counts, the
    /// last background check (proof the 15s auto-sync is alive), and the latest error if any.
    /// </summary>
    private void UpdateHeader()
    {
        var failed = _lastAttempt is { Succeeded: false } && IsConfigured;
        HeaderSyncText = _lastGoodResult is { } good
            ? $"SYNCED {good.SyncedAt.ToString("HH:mm", CultureInfo.InvariantCulture)}"
            : failed ? "SYNC FAILED" : "NOT SYNCED";
        HasSyncProblem = failed || IsStale;

        var lines = new List<string>();
        if (_lastGoodResult is { } r)
        {
            lines.Add($"Last iPhone sync {r.SyncedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}: " +
                      $"{r.Total} items (+{r.Added} new, {r.Updated} updated, {r.Completed} completed, {r.Removed} removed).");
            if (r.ExportedAt is { } exported)
            {
                lines.Add($"iPhone export written {Ago(DateTimeOffset.Now - exported).ToLowerInvariant()}" +
                          (IsStale ? " (stale: run the LTFI Export Shortcut)." : "."));
            }
        }
        else
        {
            lines.Add(IsConfigured || Location.Length == 0
                ? "No iPhone reminders sync yet."
                : $"No iPhone export found. Expected at: {Location}");
        }

        if (failed)
        {
            lines.Add($"Last attempt failed: {_lastAttempt!.Error ?? "unknown error"}");
        }

        lines.Add(_lastCheckedAt is { } checkedAt
            ? $"Auto-check every 15s; last checked {checkedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}."
            : "Auto-check every 15s.");
        lines.Add("Click SYNC to re-read the export and refresh this page.");
        HeaderSyncTip = string.Join(Environment.NewLine, lines);
    }

    private static ReminderRow ToRow(TaskItem task, DateTime today)
    {
        var (dueText, brush) = task.DueAt is { } due
            ? DueLabel(due.LocalDateTime.Date, today)
            : ("—", CcBrush.Faint);

        var priority = task.Priority switch
        {
            TaskPriority.Urgent => "!!!",
            TaskPriority.High => "!!",
            TaskPriority.Low => "·",
            _ => string.Empty
        };

        return new ReminderRow(task.Title, dueText, brush, priority, task.IsPendingOnPhone);
    }

    private static (string, IBrush) DueLabel(DateTime due, DateTime today)
    {
        var days = (due - today).Days;
        return days switch
        {
            < 0 => ($"{-days}d late", CcBrush.Red),
            0 => ("today", CcBrush.Amber),
            1 => ("tmrw", CcBrush.Body),
            < 7 => (due.ToString("ddd", CultureInfo.InvariantCulture).ToLowerInvariant(), CcBrush.Body),
            _ => (due.ToString("MMM dd", CultureInfo.InvariantCulture).ToLowerInvariant(), CcBrush.Dim)
        };
    }

    private static string Ago(TimeSpan span) => span.TotalMinutes switch
    {
        < 1 => "JUST NOW",
        < 60 => $"{(int)span.TotalMinutes}M AGO",
        < 60 * 48 => $"{(int)span.TotalHours}H AGO",
        _ => $"{(int)span.TotalDays}D AGO"
    };
}
