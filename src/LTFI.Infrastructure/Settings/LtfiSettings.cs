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
    /// iCloud for Windows exposes the Shortcuts app's iCloud Drive folder as either
    /// <c>%USERPROFILE%\iCloudDrive\Shortcuts</c> or <c>%USERPROFILE%\iCloud Drive\Shortcuts</c>
    /// depending on the build; the Shortcut saves into its <c>LTFI</c> subfolder.
    /// </summary>
    public static IReadOnlyList<string> DefaultRemindersCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = new[]
        {
            Path.Combine(home, "iCloudDrive", "Shortcuts", "LTFI"),
            Path.Combine(home, "iCloud Drive", "Shortcuts", "LTFI")
        };

        return folders
            .SelectMany(f => new[] { Path.Combine(f, "reminders.json"), Path.Combine(f, "reminders.jsonl") })
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
}
