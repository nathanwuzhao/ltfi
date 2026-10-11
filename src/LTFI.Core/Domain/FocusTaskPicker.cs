using System;
using System.Collections.Generic;
using System.Linq;

namespace LTFI.Core.Domain;

/// <summary>
/// Which tasks the Focus page's task picker offers. Pure, so the rule is testable without the UI:
/// open tasks only; with a project selected, only that project's tasks, grouped by area
/// (area sort order, then name; no area last); with no project, every open task.
/// </summary>
public static class FocusTaskPicker
{
    /// <summary>
    /// The open tasks to offer for <paramref name="projectId"/> (null = no project selected = all).
    /// Within a group the incoming order is kept (the services return newest first).
    /// </summary>
    public static IReadOnlyList<TaskItem> Options(IEnumerable<TaskItem> tasks, Guid? projectId)
    {
        var open = tasks.Where(t => t.IsOpen);
        if (projectId is not { } id)
        {
            return open.ToList();
        }

        return open
            .Where(t => t.ProjectId == id)
            .Select((t, i) => (t, i))
            .OrderBy(x => x.t.Area is null ? 1 : 0)
            .ThenBy(x => x.t.Area?.SortOrder ?? 0)
            .ThenBy(x => x.t.Area?.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.i)
            .Select(x => x.t)
            .ToList();
    }

    /// <summary>
    /// Whether a task (by its project) can stay selected once <paramref name="selectedProjectId"/>
    /// is chosen: always with no project selected, otherwise only when it is that project's.
    /// </summary>
    public static bool Belongs(Guid? taskProjectId, Guid? selectedProjectId) =>
        selectedProjectId is null || taskProjectId == selectedProjectId;
}
