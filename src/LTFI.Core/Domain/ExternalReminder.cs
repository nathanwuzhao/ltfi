using System;
using System.Globalization;

namespace LTFI.Core.Domain;

/// <summary>
/// One reminder as read from an external source (an iPhone Shortcuts export, a pyicloud sidecar,
/// …), already normalised by the source. The sync service upserts these onto
/// <see cref="TaskItem"/> rows keyed by <see cref="ExternalId"/>.
/// </summary>
public sealed record ExternalReminder(
    string ExternalId,
    string Title,
    string? ListName = null,
    string? Notes = null,
    DateTimeOffset? DueAt = null,
    TaskPriority Priority = TaskPriority.Medium,
    bool IsCompleted = false,
    DateTimeOffset? CompletedAt = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? ModifiedAt = null);

/// <summary>
/// Pure rules shared by every reminder producer so they key and map items identically
/// (contract <c>ltfi.reminders/v1</c>, see docs/reminders-sync-setup.md).
/// </summary>
public static class ReminderRules
{
    /// <summary>Stored in <see cref="TaskItem.ExternalSource"/> and <see cref="EvidenceItem.Source"/>.</summary>
    public const string SourceKey = "icloud-reminders";

    /// <summary>
    /// The key for a reminder with no real id: <c>sc:list|creationDate|title</c> (lower-cased, date
    /// in UTC rounded to the second). Deliberately an unhashed plain string so an iPhone Shortcut can
    /// compute the same value for future write-back. Renaming or moving a reminder changes the key,
    /// which the sync sees as a removal plus an add — acceptable for a mirror.
    /// </summary>
    public static string ComposeKey(string? listName, DateTimeOffset? createdAt, string title)
    {
        var created = createdAt is { } c
            ? c.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : string.Empty;
        return $"sc:{(listName ?? string.Empty).Trim().ToLowerInvariant()}|{created}|{title.Trim().ToLowerInvariant()}";
    }

    /// <summary>
    /// Maps Apple's priority (numeric 0 none / 1 high / 5 medium / 9 low, as pyicloud reports it,
    /// or the Shortcuts text "None/Low/Medium/High") onto <see cref="TaskPriority"/>. No priority
    /// maps to Medium (the LTFI default); a flagged high-priority reminder becomes Urgent.
    /// </summary>
    public static TaskPriority MapPriority(string? raw, bool isFlagged)
    {
        var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        var priority = value switch
        {
            "1" or "2" or "3" or "high" or "!!!" => TaskPriority.High,
            "4" or "5" or "6" or "medium" or "!!" => TaskPriority.Medium,
            "7" or "8" or "9" or "low" or "!" => TaskPriority.Low,
            _ => TaskPriority.Medium
        };

        return isFlagged && priority == TaskPriority.High ? TaskPriority.Urgent : priority;
    }
}
