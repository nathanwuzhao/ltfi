using System;
using System.Collections.Generic;
using System.Linq;

namespace LTFI.Core.Domain;

/// <summary>
/// Derives a project's progress percentage from the completion of its tasks and subtasks,
/// rather than a manually entered number. A task counts as fully done when Completed; a task
/// with subtasks counts by the fraction of its subtasks completed; everything else counts as 0.
/// Canceled tasks are excluded. Returns null when there are no countable tasks.
/// </summary>
public static class ProjectProgress
{
    public static int? Calculate(IEnumerable<TaskItem> tasks)
    {
        var countable = tasks.Where(t => t.Status != TaskStatus.Canceled).ToList();
        if (countable.Count == 0)
        {
            return null;
        }

        var total = countable.Sum(TaskFraction);
        return (int)Math.Round(100 * total / countable.Count);
    }

    private static double TaskFraction(TaskItem task)
    {
        if (task.Status == TaskStatus.Completed)
        {
            return 1.0;
        }

        if (task.Subtasks.Count > 0)
        {
            return (double)task.Subtasks.Count(s => s.IsCompleted) / task.Subtasks.Count;
        }

        return 0.0;
    }
}
