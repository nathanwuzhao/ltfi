using System.Text.Json;
using System.Text.Json.Serialization;
using LTFI.Core.Domain;
using LTFI.Infrastructure.Persistence;

namespace LTFI.Infrastructure.Settings;

/// <summary>
/// User-editable app settings, stored as <c>%AppData%/LTFI/settings.json</c>. Deliberately a plain
/// JSON file (no Settings UI yet): it is written with defaults on first run so it is easy to find.
/// </summary>
public sealed class LtfiSettings
{
    public RemindersSettings Reminders { get; set; } = new();

    public FocusSettings Focus { get; set; } = new();

    public SoundSettings Sounds { get; set; } = new();

    public CheckInSettings CheckIn { get; set; } = new();
}

/// <summary>
/// The weekly check-in schedule (<c>"checkIn"</c> in settings.json). A week is Monday 00:00 →
/// Sunday 23:59 local and a check-in reviews the week that is ending. Days are English day names
/// ("Saturday"), times are 24h "HH:mm". The moments must be in week order (opens ≤ gateFrom ≤ due);
/// anything unparsable or out of order falls back to the defaults as a whole.
/// </summary>
public sealed class CheckInSettings
{
    /// <summary>The check-in window opens (form available, amber header chip).</summary>
    public string OpensDay { get; set; } = "Saturday";

    public string OpensTime { get; set; } = "00:00";

    /// <summary>From here until submitted the app is gated (snoozable).</summary>
    public string GateFromDay { get; set; } = "Sunday";

    public string GateFromTime { get; set; } = "18:00";

    /// <summary>The last on-time minute; one minute later the check-in is overdue (red chip, gate).</summary>
    public string DueDay { get; set; } = "Sunday";

    public string DueTime { get; set; } = "23:59";

    /// <summary>Snoozes allowed per reviewed week.</summary>
    public int MaxSnoozesPerWeek { get; set; } = ProjectPolicy.MaxCheckInSnoozesPerWeek;

    /// <summary>Length of one snooze, in hours.</summary>
    public int SnoozeHours { get; set; } = ProjectPolicy.CheckInSnoozeHours;

    /// <summary>The validated schedule, or <see cref="CheckInSchedule.Default"/> when any value is invalid.</summary>
    public CheckInSchedule ToSchedule()
    {
        try
        {
            if (TryParse(OpensDay, OpensTime, out var opens)
                && TryParse(GateFromDay, GateFromTime, out var gate)
                && TryParse(DueDay, DueTime, out var due))
            {
                return new CheckInSchedule(opens, gate, due, MaxSnoozesPerWeek, SnoozeHours);
            }
        }
        catch (ArgumentException)
        {
            // out of order / out of range: use the defaults
        }

        return CheckInSchedule.Default;
    }

    private static bool TryParse(string? day, string? time, out TimeSpan offset)
    {
        offset = default;
        if (!Enum.TryParse<DayOfWeek>(day?.Trim(), ignoreCase: true, out var dow)
            || !Enum.IsDefined(dow)
            || int.TryParse(day, out _)
            || !TimeOnly.TryParseExact(time?.Trim(), ["HH:mm", "H:mm"], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var t))
        {
            return false;
        }

        offset = CheckInSchedule.At(dow, t.Hour, t.Minute);
        return true;
    }
}

/// <summary>In-app notification sounds (<c>"sounds"</c> in settings.json).</summary>
public sealed class SoundSettings
{
    /// <summary>Master switch for the chimes (pomodoro, NSDR, sync pings). Does not affect NSDR audio.</summary>
    public bool SoundsEnabled { get; set; } = true;

    /// <summary>Playback volume 0–100 for chimes and in-app NSDR audio (best effort; clamped).</summary>
    public int SoundVolume { get; set; } = 60;

    /// <summary>Play a soft ping when a reminders sync brings changes from the iPhone.</summary>
    public bool SyncPings { get; set; } = true;
}

public sealed class FocusSettings
{
    /// <summary>
    /// Optional guided NSDR audio: an http(s) link (the NSDR panel shows OPEN AUDIO, which opens it
    /// in the browser) or an absolute path / file: URI to a local audio file (played in-app when an
    /// NSDR starts, with a PLAY/PAUSE toggle). Null = on-screen guide only.
    /// </summary>
    public string? NsdrAudioUrl { get; set; }
}

public sealed class RemindersSettings
{
    /// <summary>
    /// Full path to the reminders export (<c>reminders.json</c> or <c>reminders.jsonl</c>).
    /// Null = auto-detect in the iCloud for Windows folders (see <see cref="SettingsStore.ResolveRemindersPath"/>).
    /// </summary>
    public string? SnapshotPath { get; set; }

    /// <summary>
    /// The standing project (exempt from the active limit) that reminders land in by default:
    /// an unmapped list "X" becomes area "X" of this project. Created on demand by the sync.
    /// </summary>
    public string StandingProject { get; set; } = "Life";

    /// <summary>
    /// The Reminders list LTFI puts new reminders in when the task's project is not the standing
    /// one (for standing-project tasks the area name is the list).
    /// </summary>
    public string LtfiList { get; set; } = "LTFI";

    /// <summary>
    /// Optional overrides, by list name (case-insensitive): <c>{"GATECH": {"project": "School", "area": "Classes"}}</c>.
    /// A mapped project that doesn't exist falls back to the default rule (it is not auto-created,
    /// so the sync never trips the active-project limit).
    /// </summary>
    public Dictionary<string, ReminderListMapping>? ListMap { get; set; }
}

/// <summary>Where reminders from one list are filed. A blank <see cref="Area"/> means no area.</summary>
public sealed class ReminderListMapping
{
    public string? Project { get; set; }

    public string? Area { get; set; }
}

/// <summary>Loads (and on first run creates) <see cref="LtfiSettings"/>. A broken file falls back to defaults.</summary>
public static class SettingsStore
{
    public const string FileName = "settings.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string SettingsFilePath => Path.Combine(DbPaths.AppDataDirectory, FileName);

    public static LtfiSettings Load() => Load(SettingsFilePath);

    public static LtfiSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                var defaults = new LtfiSettings();
                File.WriteAllText(path, JsonSerializer.Serialize(defaults, Json));
                return defaults;
            }

            var text = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<LtfiSettings>(text, Json) ?? new LtfiSettings();

            // A file from before the "checkIn" section existed gets it written in with the defaults,
            // so the schedule is discoverable. Best effort: a read-only file just keeps the defaults.
            if (!HasProperty(text, "checkIn"))
            {
                try
                {
                    File.WriteAllText(path, JsonSerializer.Serialize(settings, Json));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new LtfiSettings();
        }
    }

    private static bool HasProperty(string json, string name)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.EnumerateObject().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// iCloud for Windows mounts iCloud Drive at <c>%USERPROFILE%\iCloudDrive</c> or
    /// <c>%USERPROFILE%\iCloud Drive</c> depending on the build. The Shortcut saves into an
    /// <c>LTFI</c> folder at the Drive root (or, older setups, under <c>Shortcuts\LTFI</c>).
    /// Save File appends <c>.json</c> to a text output, hence <c>reminders.jsonl.json</c>.
    /// </summary>
    public static IReadOnlyList<string> DefaultRemindersCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = new[]
        {
            Path.Combine(home, "iCloudDrive", "LTFI"),
            Path.Combine(home, "iCloud Drive", "LTFI"),
            Path.Combine(home, "iCloudDrive", "Shortcuts", "LTFI"),
            Path.Combine(home, "iCloud Drive", "Shortcuts", "LTFI")
        };
        var names = new[] { "reminders.jsonl.json", "reminders.json", "reminders.jsonl" };

        return folders
            .SelectMany(f => names.Select(n => Path.Combine(f, n)))
            .ToList();
    }

    /// <summary>The configured path if set; otherwise the first default candidate that exists,
    /// else the first candidate (shown to the user as "put the file here").</summary>
    public static string ResolveRemindersPath(RemindersSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.SnapshotPath))
        {
            return Environment.ExpandEnvironmentVariables(settings.SnapshotPath.Trim());
        }

        var candidates = DefaultRemindersCandidates();
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    /// <summary>The write-back file: <c>outbox.json</c> in the same folder as the resolved export.</summary>
    public static string ResolveOutboxPath(RemindersSettings settings)
    {
        var snapshot = ResolveRemindersPath(settings);
        var folder = Path.GetDirectoryName(snapshot);
        return string.IsNullOrEmpty(folder) ? "outbox.json" : Path.Combine(folder, "outbox.json");
    }
}
