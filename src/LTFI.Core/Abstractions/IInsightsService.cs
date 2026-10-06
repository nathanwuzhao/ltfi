using System.Threading;
using System.Threading.Tasks;

namespace LTFI.Core.Abstractions;

/// <summary>
/// A small, factual daily snapshot derived from evidence (plan §2.4/§2.5).
/// <see cref="FocusStreakDays"/> counts days with a completed focus session (the Focus Debt panel);
/// <see cref="ActivityStreakDays"/> counts days with any contribution — the same number as the
/// contribution graph's "current streak" (the shell header's STREAK).
/// </summary>
public sealed record TodaySnapshot(int PointsToday, int FocusStreakDays, int ActivityStreakDays = 0);

/// <summary>Deterministic, evidence-derived insights. No LLM, no moralizing (plan §2.2/§2.4).</summary>
public interface IInsightsService
{
    Task<TodaySnapshot> GetTodaySnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>The contribution graph's current streak (consecutive days with any contribution).</summary>
    Task<int> GetActivityStreakAsync(CancellationToken cancellationToken = default);
}
