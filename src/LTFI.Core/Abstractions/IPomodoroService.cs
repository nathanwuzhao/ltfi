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
    bool IsWorkPaused)
{
    public string PhaseLabel => Pomodoro.Label(Phase);
    public bool IsBreak => Phase != PomodoroPhase.Work;
    public bool CanTakeNsdr => Phase == PomodoroPhase.LongBreak && !IsBreakOver;
}

/// <summary>What <see cref="IPomodoroService.AdvanceAsync"/> did on this tick.</summary>
public enum PomodoroTransition
{
    None,
    /// <summary>A work interval hit 0: counted, session paused, break countdown started.</summary>
    WorkEnded,
    /// <summary>A break (or NSDR) hit 0: waiting for the user to start the next pomodoro.</summary>
    BreakEnded
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

    /// <summary>Ends the current break (or NSDR) now and starts the next work interval.</summary>
    Task SkipBreakAsync(CancellationToken cancellationToken = default);

    /// <summary>After a break is over: resume the session and start the next work interval.</summary>
    Task StartNextAsync(CancellationToken cancellationToken = default);

    /// <summary>During a long break: swap it for a 10-minute NSDR.</summary>
    void TakeNsdrInstead();

    /// <summary>Forgets the run (the session was finished or abandoned). Stops any NSDR in progress.</summary>
    void Reset();
}
