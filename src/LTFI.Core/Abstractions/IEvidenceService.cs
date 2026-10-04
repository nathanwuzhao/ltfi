using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LTFI.Core.Domain;

namespace LTFI.Core.Abstractions;

/// <summary>One evidence record enriched with its project's title, for display in feeds/timelines.</summary>
public sealed record EvidenceLine(
    Guid Id,
    EvidenceType Type,
    string Source,
    string Title,
    string? Summary,
    Guid? ProjectId,
    string? ProjectTitle,
    DateTimeOffset OccurredAt);

/// <summary>Evidence-event count for a single local day (drives activity heatmaps/lanes).</summary>
public sealed record DayActivity(DateOnly Day, int Count);

/// <summary>
/// Read-only queries over the evidence store (plan §2.2 "evidence over vibes", §4.7 timeline).
/// Evidence is written by the task/subtask/focus services; this exposes it for the Command
/// Center's feed, activity heatmap, and per-project momentum/progress views. Time filtering runs
/// in memory — SQLite can't range/order on <see cref="DateTimeOffset"/> (see Phase 1 notes).
/// </summary>
public interface IEvidenceService
{
    /// <summary>Most recent evidence first, capped at <paramref name="limit"/>, with project titles.</summary>
    Task<IReadOnlyList<EvidenceLine>> GetRecentAsync(int limit = 40, CancellationToken cancellationToken = default);

    /// <summary>All evidence that occurred on one <em>local</em> calendar day, most recent first, with
    /// project titles (the contribution graph's click-a-day filter).</summary>
    Task<IReadOnlyList<EvidenceLine>> GetForDayAsync(DateOnly day, CancellationToken cancellationToken = default);

    /// <summary>Per-day evidence counts across all sources for the trailing <paramref name="days"/> days
    /// (oldest first, one entry per day including zero-activity days).</summary>
    Task<IReadOnlyList<DayActivity>> GetDailyActivityAsync(int days, CancellationToken cancellationToken = default);

    /// <summary>Per-day weighted contribution points (<see cref="EvidencePoints.ForContribution"/>)
    /// and contributing-event counts for the trailing <paramref name="days"/> days, oldest first,
    /// one entry per day including zeros. Drives the contribution graph.</summary>
    Task<IReadOnlyList<DayScore>> GetDailyScoresAsync(int days, CancellationToken cancellationToken = default);

    /// <summary>Per-day evidence counts for one project over the trailing <paramref name="days"/> days.</summary>
    Task<IReadOnlyList<DayActivity>> GetProjectDailyActivityAsync(
        Guid projectId, int days, CancellationToken cancellationToken = default);
}
