using System.Globalization;
using LTFI.Core.Abstractions;

namespace LTFI.Infrastructure.Reminders;

/// <summary>
/// Reads the reminders export an iPhone Shortcut saves to iCloud Drive, as synced to this PC by
/// iCloud for Windows. The path is re-resolved on every probe, so installing iCloud (or the first
/// export landing) is picked up without a restart. Polled rather than watched:
/// FileSystemWatcher is unreliable on iCloud placeholder files.
/// </summary>
public sealed class FileReminderSource(Func<string> resolvePath) : IReminderSource
{
    private const int ReadAttempts = 3;

    private readonly Func<string> _resolvePath = resolvePath;

    public FileReminderSource(string path) : this(() => path)
    {
    }

    public ReminderSourceProbe Probe()
    {
        var path = _resolvePath();
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new ReminderSourceProbe(false, path, null);
            }

            var version = string.Create(CultureInfo.InvariantCulture,
                $"{path}|{info.LastWriteTimeUtc.Ticks}|{info.Length}");
            return new ReminderSourceProbe(true, path, version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new ReminderSourceProbe(false, path, null);
        }
    }

    public async Task<ReminderSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var path = _resolvePath();
        if (!File.Exists(path))
        {
            throw new ReminderSourceException($"No reminders file at {path}.");
        }

        // iCloud may be mid-write: share read/write and retry briefly on IO errors.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var text = await reader.ReadToEndAsync(cancellationToken);
                return ReminderJsonParser.Parse(text);
            }
            catch (IOException) when (attempt < ReadAttempts)
            {
                await Task.Delay(250 * attempt, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ReminderSourceException($"Could not read {path}: {ex.Message}", ex);
            }
        }
    }
}
