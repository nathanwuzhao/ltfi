using System;
using System.Threading;
using System.Threading.Tasks;
using LTFI.Core.Domain;

namespace LTFI.Core.Abstractions;

/// <summary>A live view of a running NSDR.</summary>
public sealed record NsdrSnapshot(TimeSpan Elapsed, TimeSpan Remaining, int CueIndex, NsdrCue Cue, NsdrCue? NextCue)
{
    public bool IsDue => Remaining <= TimeSpan.Zero;
}

/// <summary>
/// Runs a single 10-minute NSDR (standalone, or in place of a pomodoro long break). Held in memory
/// by a singleton so it survives navigation. Only a run that reaches the full 10:00 records
/// <see cref="EvidenceType.NsdrCompleted"/>; stopping early records nothing.
/// </summary>
public interface INsdrService
{
    bool IsRunning { get; }

    /// <summary>Current elapsed/remaining and guide cue, or null if no NSDR is running.</summary>
    NsdrSnapshot? GetSnapshot();

    /// <summary>Starts a fresh NSDR (restarts one already running).</summary>
    void Start(Guid? focusSessionId = null);

    /// <summary>Stops early. Writes nothing.</summary>
    void Stop();

    /// <summary>
    /// If the running NSDR has reached its full duration, records the evidence item (once) and
    /// clears the run. Returns true only on the call that completed it.
    /// </summary>
    Task<bool> CompleteIfDueAsync(CancellationToken cancellationToken = default);
}
