using System;
using System.Threading;
using System.Threading.Tasks;
using LTFI.Core.Domain;

namespace LTFI.Core.Abstractions;

/// <summary>A live view of the running pomodoro: phase, countdown, and progress through the cycle.</summary>
public sealed record PomodoroSnapshot(
    PomodoroPhase Phase,
    TimeSpan Remaining,
    int CompletedWork,
    int DotsFilled,
    string Dots,
    bool IsBreakOver,
    bool IsWorkPaused,
    bool IsNsdrFromWork = false)
{
    public string PhaseLabel => Pomodoro.Label(Phase);

    /// <summary>Not working: a break or an NSDR (the session clock is paused).</summary>
    public bool IsBreak => Phase != PomodoroPhase.Work;

    /// <summary>The long break's TAKE NSDR INSTEAD offer.</summary>
    public bool CanTakeNsdr => Phase == PomodoroPhase.LongBreak && !IsBreakOver;

    /// <summary>An NSDR can be started now (any work interval or break; not during an NSDR).</summary>
    public bool CanStartNsdr => Phase != PomodoroPhase.Nsdr;
}

/// <summary>What <see cref="IPomodoroService.AdvanceAsync"/> did on this tick.</summary>
public enum PomodoroTransition
{
    None,
    /// <summary>A work interval hit 0: counted, session paused, break countdown started.</summary>
    WorkEnded,
    /// <summary>A break (or NSDR) hit 0: waiting for the user to start the next pomodoro.</summary>
    BreakEnded,
    /// <summary>
    /// An NSDR taken during a work interval reached 10:00 (evidence recorded): back to the same
    /// interval, still paused at its remaining time, waiting for RESUME WORK.
    /// </summary>
    NsdrEnded
}

/// <summary>
/// Pomodoro timing on top of the single focus session: one run = one <see cref="FocusSession"/>,
/// paused during breaks so only work time is tracked. A singleton holding the phase in memory so
/// the countdown survives navigation. Driven by a UI tick calling <see cref="AdvanceAsync"/>.
/// </summary>
public interface IPomodoroService
{
    /// <summary>True while a pomodoro run is attached to the active focus session.</summary>
    bool IsActive { get; }

    PomodoroSnapshot? GetSnapshot();

    /// <summary>Starts a focus session in pomodoro mode (first work interval).</summary>
    Task StartAsync(Guid? projectId, Guid? taskId, string? intent, CancellationToken cancellationToken = default);

    /// <summary>Checks the countdown and performs any due transition. Safe to call every second.</summary>
    Task<PomodoroTransition> AdvanceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the current break (or NSDR) now and starts the next work interval. For an NSDR taken
    /// during a work interval it ends the NSDR (nothing recorded) and returns to that interval,
    /// still paused (RESUME WORK continues it).
    /// </summary>
    Task SkipBreakAsync(CancellationToken cancellationToken = default);

    /// <summary>After a break is over: resume the session and start the next work interval.</summary>
    Task StartNextAsync(CancellationToken cancellationToken = default);

    /// <summary>During a long break: swap it for a 10-minute NSDR.</summary>
    void TakeNsdrInstead();

    /// <summary>
    /// Starts a 10-minute NSDR inside the active focus session, linked to it (its evidence carries
    /// the session id). Pomodoro work interval: the session is paused (NSDR time is not work time)
    /// and the interval resumes afterwards at its remaining time. Pomodoro break: the NSDR replaces
    /// the rest of the break. FREE session: the session is paused and stays paused afterwards.
    /// Returns false when there is no session or an NSDR is already running.
    /// </summary>
    Task<bool> TakeNsdrAsync(CancellationToken cancellationToken = default);

    /// <summary>Forgets the run (the session was finished or abandoned). Stops any NSDR in progress.</summary>
    void Reset();
}
