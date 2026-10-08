using System;
using System.Collections.Generic;
using System.Linq;

namespace LTFI.Core.Domain;

/// <summary>How the Tasks list is split into sections.</summary>
public enum TaskGrouping
{
    /// <summary>Agenda: Overdue / Today / Tomorrow / This week / Later / No due date.</summary>
    Due,

    /// <summary>One section per area (#HOMEWORK …), tasks without an area last.</summary>
    Area,

    /// <summary>A single flat list.</summary>
    None
}

/// <summary>Order of tasks within a section (or the whole list when not grouped).</summary>
public enum TaskSort
{
    /// <summary>Due date (no date last), then priority Urgent→Low, then title.</summary>
    Due,

    /// <summary>Priority Urgent→Low, then due date, then title.</summary>
    Priority,

    /// <summary>Title A→Z, then due date.</summary>
    Title
}

/// <summary>Where an open task's due date falls relative to today (local calendar days).</summary>
public enum DueBucket
{
    Overdue,
    Today,
    Tomorrow,
    ThisWeek,
    Later,
    NoDate
}

/// <summary>One section of the Tasks list. <see cref="IsAlert"/> marks the overdue header (red).</summary>
public sealed record TaskGroup(string Key, string Label, bool IsAlert, IReadOnlyList<TaskItem> Tasks);

/// <summary>
/// Pure grouping/sorting for the Tasks list (no UI, no clock): the caller passes "today" and the
/// time zone the due dates are read in, so the bucket boundaries are testable.
/// </summary>
public static class TaskAgenda
{
    public const string CompletedKey = "completed";

    /// <summary>
    /// Buckets a due date by local calendar day: before today → Overdue, today, +1 → Tomorrow,
    /// +2…+7 → This week, +8 and beyond → Later, none → NoDate.
    /// </summary>
    public static DueBucket BucketOf(DateTimeOffset? due, DateOnly today, TimeZoneInfo? zone = null)
    {
        if (due is not { } value)
        {
            return DueBucket.NoDate;
        }

        var day = LocalDay(value, zone);
        var delta = day.DayNumber - today.DayNumber;
        return delta switch
        {
            < 0 => DueBucket.Overdue,
            0 => DueBucket.Today,
            1 => DueBucket.Tomorrow,
            <= 7 => DueBucket.ThisWeek,
            _ => DueBucket.Later
        };
    }

    public static string LabelOf(DueBucket bucket) => bucket switch
    {
        DueBucket.Overdue => "OVERDUE",
        DueBucket.Today => "TODAY",
        DueBucket.Tomorrow => "TOMORROW",
        DueBucket.ThisWeek => "THIS WEEK",
        DueBucket.Later => "LATER",
        _ => "NO DUE DATE"
    };

    /// <summary>
    /// Orders <paramref name="tasks"/> into sections. Open tasks are grouped by
    /// <paramref name="grouping"/> (empty sections omitted) and sorted by <paramref name="sort"/>;
    /// closed tasks (Completed/Canceled) present in the input go into a final COMPLETED section,
    /// most recently completed first. Filtering which tasks to show is the caller's job.
    /// </summary>
    public static IReadOnlyList<TaskGroup> Build(
        IEnumerable<TaskItem> tasks,
        DateOnly today,
        TaskGrouping grouping,
        TaskSort sort,
        TimeZoneInfo? zone = null)
    {
        var all = tasks.ToList();
        var open = Sort(all.Where(t => t.IsOpen), sort).ToList();
        var groups = new List<TaskGroup>();

        switch (grouping)
        {
            case TaskGrouping.Due:
                foreach (var bucket in Enum.GetValues<DueBucket>())
                {
                    var items = open.Where(t => BucketOf(t.DueAt, today, zone) == bucket).ToList();
                    if (items.Count > 0)
                    {
                        groups.Add(new TaskGroup(bucket.ToString(), LabelOf(bucket), bucket == DueBucket.Overdue, items));
                    }
                }
                break;

            case TaskGrouping.Area:
                var byArea = open
                    .GroupBy(t => t.AreaId)
                    .Select(g => (Id: g.Key, Name: g.First().Area?.Name, Items: g.ToList()))
                    .OrderBy(g => g.Id is null ? 1 : 0)
                    .ThenBy(g => g.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase);
                foreach (var g in byArea)
                {
                    var label = g.Id is null
                        ? "NO AREA"
                        : "#" + (string.IsNullOrWhiteSpace(g.Name) ? "AREA" : g.Name.Trim().ToUpperInvariant());
                    groups.Add(new TaskGroup(g.Id?.ToString() ?? "no-area", label, false, g.Items));
                }
                break;

            default:
                if (open.Count > 0)
                {
                    groups.Add(new TaskGroup("all", "OPEN", false, open));
                }
                break;
        }

        var closed = all
            .Where(t => !t.IsOpen)
            .OrderByDescending(t => t.CompletedAt ?? t.UpdatedAt)
            .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (closed.Count > 0)
        {
            groups.Add(new TaskGroup(CompletedKey, "COMPLETED", false, closed));
        }

        return groups;
    }

    /// <summary>Sorts tasks by the chosen order; ties always fall through to the other keys.</summary>
    public static IEnumerable<TaskItem> Sort(IEnumerable<TaskItem> tasks, TaskSort sort)
    {
        var title = StringComparer.OrdinalIgnoreCase;
        return sort switch
        {
            TaskSort.Priority => tasks
                .OrderByDescending(t => t.Priority)
                .ThenBy(t => t.DueAt is null ? 1 : 0)
                .ThenBy(t => t.DueAt)
                .ThenBy(t => t.Title, title),
            TaskSort.Title => tasks
                .OrderBy(t => t.Title, title)
                .ThenBy(t => t.DueAt is null ? 1 : 0)
                .ThenBy(t => t.DueAt),
            _ => tasks
                .OrderBy(t => t.DueAt is null ? 1 : 0)
                .ThenBy(t => t.DueAt)
                .ThenByDescending(t => t.Priority)
                .ThenBy(t => t.Title, title)
        };
    }

    private static DateOnly LocalDay(DateTimeOffset value, TimeZoneInfo? zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, zone ?? TimeZoneInfo.Local).DateTime);
}
