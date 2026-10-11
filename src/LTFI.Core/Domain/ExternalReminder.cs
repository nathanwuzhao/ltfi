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
    DateTimeOffset? ModifiedAt = null,
    string? Url = null,
    string? FallbackKey = null);

/// <summary>
/// Pure rules shared by every reminder producer so they key and map items identically
/// (contract <c>ltfi.reminders/v1</c>, see docs/reminders-sync-setup.md).
/// </summary>
public static class ReminderRules
{
    /// <summary>Stored in <see cref="TaskItem.ExternalSource"/> and <see cref="EvidenceItem.Source"/>.</summary>
    public const string SourceKey = "icloud-reminders";

    /// <summary>Prefix of the ids LTFI and the export Shortcut stamp into a reminder's URL field.</summary>
    public const string LtfiUrlPrefix = "ltfi://r/";

    /// <summary>
    /// Fallback key for a reminder with neither an <c>ltfi://</c> URL nor a real id:
    /// <c>cd:&lt;creationDate in UTC to the second&gt;</c>. When two reminders in one export share a
    /// creation date (or there is no creation date), pass the title as <paramref name="tiebreakTitle"/>
    /// to get <c>cd:&lt;date&gt;|&lt;lower-cased trimmed title&gt;</c>. The title is otherwise not part of
    /// the key, so renaming or moving a reminder keeps its identity.
    /// </summary>
    public static string ComposeKey(DateTimeOffset? createdAt, string? tiebreakTitle = null)
    {
        var key = "cd:" + FormatCreated(createdAt);
        return tiebreakTitle is null ? key : $"{key}|{tiebreakTitle.Trim().ToLowerInvariant()}";
    }

    /// <summary>The creation date as used in <see cref="ComposeKey"/> (UTC, to the second); empty for null.</summary>
    public static string FormatCreated(DateTimeOffset? createdAt) =>
        createdAt is { } c
            ? c.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : string.Empty;

    /// <summary>The iPhone list non-standing tasks go to when settings <c>reminders.ltfiList</c> is empty.</summary>
    public const string DefaultLtfiList = "LTFI";

    /// <summary>The configured LTFI list (trimmed), or <see cref="DefaultLtfiList"/> when blank.</summary>
    public static string LtfiListOrDefault(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultLtfiList : configured.Trim();

    /// <summary>
    /// The iPhone Reminders list a task LTFI creates goes into: a standing project's task goes into the
    /// list named by its area; every other task (any other project, or none) into the LTFI list.
    /// Null when a standing project has no area yet — the task can't be placed until one is picked.
    /// Shared by <c>TaskService</c> (what it writes) and the Tasks editor (what it says).
    /// </summary>
    public static string? TargetList(bool projectIsStanding, string? areaName, string? configuredLtfiList) =>
        projectIsStanding
            ? string.IsNullOrWhiteSpace(areaName) ? null : areaName
            : LtfiListOrDefault(configuredLtfiList);

    /// <summary>True for an <c>ltfi://r/…</c> id (stamped by LTFI or by the export Shortcut).</summary>
    public static bool IsLtfiUrl(string? value) =>
        value is not null
        && value.StartsWith(LtfiUrlPrefix, StringComparison.OrdinalIgnoreCase)
        && value.Length > LtfiUrlPrefix.Length;

    /// <summary>A fresh id for a reminder LTFI creates: <c>ltfi://r/&lt;32 hex digits&gt;</c>.</summary>
    public static string NewLtfiUrl() => LtfiUrlPrefix + Guid.NewGuid().ToString("N");

    /// <summary>
    /// True for an id LTFI itself minted (<see cref="NewLtfiUrl"/>: 32 hex digits), as opposed to one
    /// the export Shortcut stamped (<c>yyyyMMddHHmmss-NNNNN</c>). The sync keeps the project/area LTFI
    /// recorded for those instead of re-deriving it from the list.
    /// </summary>
    public static bool IsLtfiCreatedUrl(string? value)
    {
        if (!IsLtfiUrl(value))
        {
            return false;
        }

        var rest = value!.AsSpan(LtfiUrlPrefix.Length);
        if (rest.Length != 32)
        {
            return false;
        }

        foreach (var ch in rest)
        {
            if (!char.IsAsciiHexDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether an exported due date shows a due date LTFI pushed (an outbox <c>update</c>/<c>create</c>).
    /// Due is a calendar date (<see cref="DueDates"/>): the same LOCAL calendar day matches, whatever
    /// time either side carries (LTFI sends 23:59; the phone may hand back 23:59, 12:00, midnight or a
    /// date only). Also the same instant within a minute, whatever the offset.
    /// </summary>
    public static bool DueMatches(DateTimeOffset pushed, DateTimeOffset? actual)
    {
        if (actual is not { } a)
        {
            return false;
        }

        if ((pushed - a).Duration() < TimeSpan.FromMinutes(1))
        {
            return true;
        }

        return pushed.LocalDateTime.Date == a.LocalDateTime.Date;
    }

    /// <summary>
    /// LTFI priority → the text the iPhone's Add New Reminder takes. Medium is LTFI's "no priority"
    /// (None imports as Medium), so it goes out as None rather than putting "!!" on every reminder.
    /// </summary>
    public static string ToApplePriority(TaskPriority priority) => priority switch
    {
        TaskPriority.Urgent or TaskPriority.High => "High",
        TaskPriority.Low => "Low",
        _ => "None"
    };

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
