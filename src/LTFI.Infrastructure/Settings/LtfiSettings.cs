using System.Text.Json;
using System.Text.Json.Serialization;
using LTFI.Infrastructure.Persistence;

namespace LTFI.Infrastructure.Settings;

/// <summary>
/// User-editable app settings, stored as <c>%AppData%/LTFI/settings.json</c>. Deliberately a plain
/// JSON file (no Settings UI yet): it is written with defaults on first run so it is easy to find.
/// </summary>
public sealed class LtfiSettings
{
    public RemindersSettings Reminders { get; set; } = new();
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

            return JsonSerializer.Deserialize<LtfiSettings>(File.ReadAllText(path), Json) ?? new LtfiSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new LtfiSettings();
        }
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
