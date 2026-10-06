using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using LTFI.Infrastructure.Persistence;

namespace LTFI.Infrastructure.Reminders;

/// <summary>
/// LTFI → iPhone write-back (contract <c>ltfi.outbox/v1</c>, docs/reminders-sync-setup.md).
/// Commands are rows in <see cref="LtfiDbContext.Outbox"/>, enqueued in the same SaveChanges as the
/// task change that caused them (see the static helpers). <see cref="FlushAsync"/> rewrites
/// <c>outbox.json</c> with every unconfirmed command; the reminders sync confirms them.
/// </summary>
public sealed class ReminderOutbox(IDbContextFactory<LtfiDbContext> contextFactory, Func<string> resolvePath)
    : IReminderOutbox
{
    public const string Schema = "ltfi.outbox/v1";

    private static readonly JsonSerializerOptions FileJson = new()
    {
        WriteIndented = true,
        // Plain UTF-8 text (no \u escapes for é, —, etc.) — easier to eyeball; still valid JSON.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IDbContextFactory<LtfiDbContext> _contextFactory = contextFactory;
    private readonly Func<string> _resolvePath = resolvePath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public string Location => _resolvePath();

    public string? LastError { get; private set; }

    public async Task<int> CountPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Outbox.CountAsync(c => c.ConfirmedAt == null, cancellationToken);
    }

    public async Task<bool> FlushAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            List<OutboxCommand> pending;
            await using (var db = await _contextFactory.CreateDbContextAsync(cancellationToken))
            {
                pending = await db.Outbox.AsNoTracking()
                    .Where(c => c.ConfirmedAt == null)
                    .ToListAsync(cancellationToken);
            }

            var path = _resolvePath();
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (folder is null || !Directory.Exists(folder))
            {
                // No iCloud Drive folder yet: keep the commands in the DB; a later flush writes them.
                LastError = $"Folder for {path} doesn't exist yet.";
                return false;
            }

            var text = BuildFileJson(pending, DateTimeOffset.Now);
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, text, cancellationToken);
            File.Move(temp, path, overwrite: true); // atomic replace on the same volume
            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = $"Could not write the outbox: {ex.Message}";
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// The file contents: <c>{"schema","writtenAt","commands":[{"op","url",…flat string fields}]}</c>,
    /// commands in the order they were enqueued (a create always precedes its complete/update).
    /// </summary>
    public static string BuildFileJson(IEnumerable<OutboxCommand> commands, DateTimeOffset writtenAt)
    {
        var list = new List<Dictionary<string, string>>();
        foreach (var command in commands.OrderBy(c => c.CreatedAt).ThenBy(c => c.Op == OutboxCommand.CreateOp ? 0 : 1))
        {
            var entry = new Dictionary<string, string>
            {
                ["op"] = command.Op,
                ["url"] = command.ExternalUrl
            };

            foreach (var (key, value) in ReadPayload(command.PayloadJson))
            {
                if (key is not ("op" or "url"))
                {
                    entry[key] = value;
                }
            }

            list.Add(entry);
        }

        var file = new Dictionary<string, object>
        {
            ["schema"] = Schema,
            ["writtenAt"] = writtenAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            ["commands"] = list
        };

        return JsonSerializer.Serialize(file, FileJson);
    }

    // ------------------------------------------------------------------ enqueue helpers
    // Called inside the caller's unit of work so the task change and its command commit together.

    /// <summary>Queues "create this reminder on the iPhone" for a task LTFI just made.</summary>
    public static OutboxCommand EnqueueCreate(LtfiDbContext db, TaskItem task, DateTimeOffset now)
    {
        var command = new OutboxCommand
        {
            Op = OutboxCommand.CreateOp,
            ExternalUrl = task.ExternalId!,
            PayloadJson = CreatePayload(task),
            CreatedAt = now
        };
        db.Outbox.Add(command);
        task.ExternalPendingSince ??= now;
        return command;
    }

    /// <summary>Queues "mark this reminder completed on the iPhone".</summary>
    public static OutboxCommand EnqueueComplete(LtfiDbContext db, TaskItem task, DateTimeOffset now)
    {
        var command = new OutboxCommand
        {
            Op = OutboxCommand.CompleteOp,
            ExternalUrl = task.ExternalId!,
            PayloadJson = "{}",
            // Strictly after any create queued in the same unit of work.
            CreatedAt = now.AddTicks(1)
        };
        db.Outbox.Add(command);
        task.ExternalPendingSince ??= now;
        return command;
    }

    /// <summary>
    /// Queues "set this reminder's due date on the iPhone" with the task's current
    /// <see cref="TaskItem.DueAt"/> (which must be set). Coalesces: an unconfirmed update for the same
    /// url (<paramref name="pendingUpdate"/>) gets the new date instead of a second command.
    /// </summary>
    public static OutboxCommand EnqueueUpdate(LtfiDbContext db, TaskItem task, OutboxCommand? pendingUpdate, DateTimeOffset now)
    {
        if (task.DueAt is null)
        {
            throw new InvalidOperationException("An update command needs a due date.");
        }

        var payload = UpdatePayload(task.DueAt.Value);
        task.ExternalPendingSince ??= now;
        if (pendingUpdate is not null)
        {
            pendingUpdate.PayloadJson = payload;
            return pendingUpdate;
        }

        var command = new OutboxCommand
        {
            Op = OutboxCommand.UpdateOp,
            ExternalUrl = task.ExternalId!,
            PayloadJson = payload,
            // After any create/complete queued in the same unit of work.
            CreatedAt = now.AddTicks(2)
        };
        db.Outbox.Add(command);
        return command;
    }

    /// <summary>The update command's fields: just <c>dueDate</c> (full ISO 8601 with offset).</summary>
    public static string UpdatePayload(DateTimeOffset due) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["dueDate"] = FormatDue(due) });

    /// <summary>The <c>dueDate</c> a create/update payload carries; null when empty or unreadable.</summary>
    public static DateTimeOffset? ReadDue(string payloadJson)
    {
        foreach (var (key, value) in ReadPayload(payloadJson))
        {
            if (key == "dueDate" && !string.IsNullOrWhiteSpace(value)
                && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var due))
            {
                return due;
            }
        }

        return null;
    }

    /// <summary>The create command's fields, all flat strings (Shortcuts-friendly).</summary>
    public static string CreatePayload(TaskItem task) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["title"] = task.Title,
            ["notes"] = task.Description ?? string.Empty,
            ["list"] = task.ExternalList ?? string.Empty,
            ["dueDate"] = FormatDue(task.DueAt),
            ["priority"] = ReminderRules.ToApplePriority(task.Priority)
        });

    /// <summary>
    /// Always full ISO 8601 with offset (<c>2026-10-10T00:00:00-04:00</c>), so the Shortcut's
    /// Get Dates From Input parses every value the same way. Empty for none.
    /// </summary>
    public static string FormatDue(DateTimeOffset? due) =>
        due is { } d ? d.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) : string.Empty;

    private static IEnumerable<KeyValuePair<string, string>> ReadPayload(string json)
    {
        Dictionary<string, JsonElement>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (raw is null)
        {
            yield break;
        }

        foreach (var (key, value) in raw)
        {
            yield return new(key, value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText());
        }
    }
}
