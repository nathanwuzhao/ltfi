using System.Threading;
using System.Threading.Tasks;

namespace LTFI.Core.Abstractions;

/// <summary>A small, factual daily snapshot derived from evidence (plan §2.4/§2.5).</summary>
public sealed record TodaySnapshot(int PointsToday, int FocusStreakDays);

/// <summary>Deterministic, evidence-derived insights. No LLM, no moralizing (plan §2.2/§2.4).</summary>
public interface IInsightsService
{
    Task<TodaySnapshot> GetTodaySnapshotAsync(CancellationToken cancellationToken = default);
}
