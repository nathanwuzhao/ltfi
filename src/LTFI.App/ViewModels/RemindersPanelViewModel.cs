using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
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

    public RemindersPanelViewModel(IReminderSyncService sync, IReminderOutbox outbox)
    {
        _sync = sync;
        _outbox = outbox;
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
        try
        {
            var result = await _sync.SyncIfChangedAsync();
            if (result is not null)
            {
                await AfterSyncAsync(result);
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
            await AfterSyncAsync(await _sync.SyncAsync());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reminders sync failed");
            ErrorText = ex.Message;
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private async Task AfterSyncAsync(ReminderSyncResult result)
    {
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
