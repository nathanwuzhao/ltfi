using System.Globalization;
using System.Text.Json;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;

namespace LTFI.Infrastructure.Reminders;

/// <summary>
/// Parses the <c>ltfi.reminders/v1</c> contract (docs/reminders-sync-setup.md). Tolerant on
/// purpose, because iOS Shortcuts output varies by device/version. Accepted shapes:
/// <list type="number">
///   <item>an object <c>{"schema", "exportedAt", "source", "reminders": [...]}</c>;</item>
///   <item>a bare top-level array of reminder objects;</item>
///   <item>JSON Lines — one reminder object per line.</item>
/// </list>
/// Keys are case-insensitive, unknown keys are ignored, and only <c>title</c> is required.
/// Array elements that are themselves JSON strings (a Shortcuts quirk) are unwrapped.
/// Items without a usable title are skipped; anything structurally unreadable throws
/// <see cref="ReminderSourceException"/> so the sync keeps the last good state.
/// <para>
/// Identity (<see cref="ExternalReminder.ExternalId"/>), in order of precedence:
/// (a) a <c>url</c> starting with <c>ltfi://r/</c>, verbatim; (b) a real <c>id</c>/<c>identifier</c>;
/// (c) <see cref="ReminderRules.ComposeKey"/> from the creation date, with the title appended only
/// when two reminders in the same export share that creation date. Every item also carries its (c)
/// key as <see cref="ExternalReminder.FallbackKey"/> so the sync can adopt a row keyed before the
/// export Shortcut stamped a URL on it.
/// </para>
/// </summary>
public static class ReminderJsonParser
{
    /// <summary>A parsed item before keys are assigned (keys need the whole export for tiebreaks).</summary>
    private sealed record RawItem(ExternalReminder Reminder, string? Id);

    public static ReminderSnapshot Parse(string text)
    {
        text = text.Trim().TrimStart('﻿');
        if (text.Length == 0)
        {
            throw new ReminderSourceException("The reminders file is empty.");
        }

        List<RawItem> items;
        DateTimeOffset? exportedAt = null;
        string? producer = null;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true });
            (items, exportedAt, producer) = FromRoot(doc.RootElement);
        }
        catch (JsonException) when (text.Contains('\n'))
        {
            // Not a single JSON document — try JSON Lines.
            items = FromJsonLines(text);
        }
        catch (JsonException ex)
        {
            throw new ReminderSourceException("The reminders file is not valid JSON.", ex);
        }

        return new ReminderSnapshot(AssignKeys(items), exportedAt, producer);
    }

    /// <summary>Applies the key precedence (see the class summary) across the whole export.</summary>
    private static List<ExternalReminder> AssignKeys(List<RawItem> items)
    {
        // Creation dates shared by 2+ reminders in this export need the title as a tiebreak.
        var createdCounts = items
            .GroupBy(i => ReminderRules.FormatCreated(i.Reminder.CreatedAt), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var keyed = new List<ExternalReminder>(items.Count);
        foreach (var (reminder, id) in items)
        {
            var created = ReminderRules.FormatCreated(reminder.CreatedAt);
            var collides = created.Length == 0 || createdCounts[created] > 1;
            var fallback = ReminderRules.ComposeKey(reminder.CreatedAt, collides ? reminder.Title : null);

            var externalId = ReminderRules.IsLtfiUrl(reminder.Url) ? reminder.Url!
                : !string.IsNullOrWhiteSpace(id) ? id.Trim()
                : fallback;

            keyed.Add(reminder with { ExternalId = externalId, FallbackKey = fallback });
        }

        return keyed;
    }

    private static (List<RawItem> Items, DateTimeOffset? ExportedAt, string? Producer) FromRoot(JsonElement root)
    {
        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                return (ReadItems(root.EnumerateArray()), null, null);

            case JsonValueKind.Object:
                var props = Props(root);
                if (props.TryGetValue("reminders", out var list))
                {
                    // Some Shortcuts builds hand back the Repeat Results as one newline-joined string.
                    var items = list.ValueKind switch
                    {
                        JsonValueKind.Array => ReadItems(list.EnumerateArray()),
                        JsonValueKind.String => FromJsonLines(list.GetString() ?? string.Empty),
                        JsonValueKind.Null => [],
                        _ => throw new ReminderSourceException("\"reminders\" must be an array.")
                    };
                    return (items, Date(props, "exportedAt"), Text(props, "source"));
                }

                // A single bare reminder object (a one-line JSONL file).
                return (ReadItems([root]), null, null);

            default:
                throw new ReminderSourceException("The reminders file must contain a JSON object or array.");
        }
    }

    private static List<RawItem> FromJsonLines(string text)
    {
        var items = new List<RawItem>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd(',');
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                items.AddRange(ReadItems([doc.RootElement]));
            }
            catch (JsonException ex)
            {
                throw new ReminderSourceException("The reminders file has a line that is not valid JSON.", ex);
            }
        }

        return items;
    }

    private static List<RawItem> ReadItems(IEnumerable<JsonElement> elements)
    {
        var items = new List<RawItem>();
        foreach (var element in elements)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                // A reminder dictionary serialised as text inside the array.
                var inner = element.GetString();
                if (string.IsNullOrWhiteSpace(inner))
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(inner);
                    items.AddRange(ReadItems([doc.RootElement]));
                }
                catch (JsonException ex)
                {
                    throw new ReminderSourceException("A reminder entry is not valid JSON.", ex);
                }

                continue;
            }

            if (element.ValueKind == JsonValueKind.Object && ReadItem(element) is { } item)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private static RawItem? ReadItem(JsonElement element)
    {
        var props = Props(element);
        var title = Text(props, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        title = title.Trim();
        var listName = ListName(props);
        var created = Date(props, "creationDate") ?? Date(props, "createdAt");
        var isFlagged = Bool(props, "isFlagged") ?? Bool(props, "flagged") ?? false;
        var isCompleted = Bool(props, "isCompleted") ?? Bool(props, "completed") ?? false;

        var id = Text(props, "id") ?? Text(props, "identifier");

        // ExternalId/FallbackKey are assigned once the whole export is read (AssignKeys).
        var reminder = new ExternalReminder(
            string.Empty,
            title,
            listName,
            Text(props, "notes"),
            Date(props, "dueDate"),
            ReminderRules.MapPriority(Text(props, "priority"), isFlagged),
            isCompleted,
            isCompleted ? Date(props, "completionDate") : null,
            created,
            Date(props, "lastModifiedDate") ?? Date(props, "modifiedAt"),
            Url: Text(props, "url"));

        return new RawItem(reminder, id);
    }

    /// <summary>Case-insensitive view over an object's properties (last duplicate wins).</summary>
    private static Dictionary<string, JsonElement> Props(JsonElement obj)
    {
        var props = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in obj.EnumerateObject())
        {
            props[p.Name] = p.Value;
        }

        return props;
    }

    /// <summary>A trimmed string (numbers/bools rendered as text); null for missing or blank.</summary>
    private static string? Text(Dictionary<string, JsonElement> props, string key)
    {
        if (!props.TryGetValue(key, out var v))
        {
            return null;
        }

        var s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };

        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    /// <summary>The list name; Shortcuts may emit it as text or as an object with a name/title.</summary>
    private static string? ListName(Dictionary<string, JsonElement> props)
    {
        if (props.TryGetValue("list", out var v) && v.ValueKind == JsonValueKind.Object)
        {
            var inner = Props(v);
            return Text(inner, "name") ?? Text(inner, "title");
        }

        return Text(props, "list") ?? Text(props, "listName");
    }

    /// <summary>Booleans arrive as true/false, "Yes"/"No", "true"/"false", or 1/0.</summary>
    private static bool? Bool(Dictionary<string, JsonElement> props, string key)
    {
        if (!props.TryGetValue(key, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetInt32(out var n) ? n != 0 : null,
            JsonValueKind.String => (v.GetString() ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "true" or "yes" or "1" => true,
                "false" or "no" or "0" or "" => false,
                _ => null
            },
            _ => null
        };
    }

    /// <summary>ISO 8601 (with or without offset). A date-only value means local midnight (all-day).
    /// Empty strings — the guard the Shortcut uses for "no date" — are null.</summary>
    private static DateTimeOffset? Date(Dictionary<string, JsonElement> props, string key)
    {
        var s = Text(props, key);
        if (s is null)
        {
            return null;
        }

        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : null;
    }
}
