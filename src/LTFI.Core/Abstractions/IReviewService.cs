using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LTFI.Core.Abstractions;

/// <summary>Per-project activity over the review window.</summary>
public sealed record ProjectActivityLine(string Title, TimeSpan FocusTime, int TasksCompleted);

/// <summary>An active project that hasn't seen activity recently (plan §3.4/§7.3).</summary>
public sealed record StalledProjectLine(string Title, int DaysSinceActivity);

/// <summary>
/// A deterministic, local-data weekly review (plan §3.6). No LLM — just counts and sums over the
/// trailing seven days, plus stalled-project detection.
/// </summary>
public sealed record WeeklyReview(
    int ActiveProjectCount,
    int MaxActiveProjects,
    bool IsOverLimit,
    int TasksCompletedThisWeek,
    TimeSpan FocusTimeThisWeek,
    int NewProjectsThisWeek,
    int ArchivedProjectsThisWeek,
    IReadOnlyList<ProjectActivityLine> ProjectActivity,
    IReadOnlyList<StalledProjectLine> StalledProjects);

public interface IReviewService
{
    Task<WeeklyReview> GetWeeklyReviewAsync(CancellationToken cancellationToken = default);
}
