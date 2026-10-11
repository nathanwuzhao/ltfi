using System;

namespace LTFI.Core.Domain;

/// <summary>How the Focus page times a session: pomodoro countdown intervals, or a free count-up.</summary>
public enum FocusTimerMode
{
    Pomodoro,
    Free
}

/// <summary>
/// The phase a pomodoro run is in. <see cref="Nsdr"/> replaces the rest of a break, or pauses a work
/// interval (<see cref="PomodoroCycle.NsdrFromWork"/>), when chosen.
/// </summary>
public enum PomodoroPhase
{
    Work,
    ShortBreak,
    LongBreak,
    Nsdr
}

/// <summary>Pomodoro timing constants (classic 25 / 5 / 15, long break after every 4th work interval).</summary>
public static class Pomodoro
{
    public static readonly TimeSpan WorkDuration = TimeSpan.FromMinutes(25);
    public static readonly TimeSpan ShortBreakDuration = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LongBreakDuration = TimeSpan.FromMinutes(15);

    /// <summary>Work intervals per cycle; the break after the last one is a long break.</summary>
    public const int IntervalsPerCycle = 4;

    public static TimeSpan DurationOf(PomodoroPhase phase) => phase switch
    {
        PomodoroPhase.Work => WorkDuration,
        PomodoroPhase.ShortBreak => ShortBreakDuration,
        PomodoroPhase.LongBreak => LongBreakDuration,
        PomodoroPhase.Nsdr => Nsdr.Duration,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
    };

    public static string Label(PomodoroPhase phase) => phase switch
    {
        PomodoroPhase.Work => "WORK",
        PomodoroPhase.ShortBreak => "BREAK",
        PomodoroPhase.LongBreak => "LONG BREAK",
        PomodoroPhase.Nsdr => "NSDR",
        _ => phase.ToString().ToUpperInvariant()
    };
}

/// <summary>What a focus session offers once an NSDR taken inside it ends (completed or stopped).</summary>
public enum NsdrReturn
{
    /// <summary>Standalone NSDR (no session): nothing to go back to.</summary>
    None,
    /// <summary>FREE mode: the session stays paused; RESUME continues it.</summary>
    ResumeSession,
    /// <summary>Pomodoro work interval: work stays paused at its remaining time; RESUME WORK.</summary>
    ResumeWork,
    /// <summary>Pomodoro break: the NSDR replaced the rest of it; START NEXT POMODORO.</summary>
    StartNextPomodoro
}

/// <summary>
/// The pure pomodoro state machine: which phase a run is in and how many work intervals it has
/// completed. Immutable — each transition returns the next state. Timing is the caller's job.
/// <see cref="NsdrFromWork"/> marks an NSDR taken during a work interval (the interval is paused,
/// not ended, and resumes afterwards) as opposed to one replacing a break.
/// </summary>
public sealed record PomodoroCycle(PomodoroPhase Phase, int CompletedWork, bool NsdrFromWork = false)
{
    /// <summary>A fresh run: first work interval, nothing completed.</summary>
    public static PomodoroCycle Start() => new(PomodoroPhase.Work, 0);

    /// <summary>Not working (a break, or an NSDR). The session clock is paused throughout.</summary>
    public bool IsBreak => Phase != PomodoroPhase.Work;

    /// <summary>Rest that follows a completed interval (a break, or an NSDR in place of one).</summary>
    public bool IsRestAfterWork => IsBreak && !NsdrFromWork;

    /// <summary>An NSDR can start from any work interval or break, but not inside another NSDR.</summary>
    public bool CanTakeNsdr => Phase != PomodoroPhase.Nsdr;

    /// <summary>
    /// Start a 10-minute NSDR inside the run. From a work interval it pauses the interval (its
    /// remaining time is kept and resumed afterwards — NSDR time is not work time); from a short or
    /// long break it replaces the rest of the break.
    /// </summary>
    public PomodoroCycle TakeNsdr() => Phase switch
    {
        PomodoroPhase.Work => this with { Phase = PomodoroPhase.Nsdr, NsdrFromWork = true },
        PomodoroPhase.ShortBreak or PomodoroPhase.LongBreak => this with { Phase = PomodoroPhase.Nsdr, NsdrFromWork = false },
        _ => throw new InvalidOperationException("An NSDR is already running.")
    };

    /// <summary>What to offer when the current NSDR ends; <see cref="NsdrReturn.None"/> outside an NSDR.</summary>
    public NsdrReturn AfterNsdr => Phase != PomodoroPhase.Nsdr
        ? NsdrReturn.None
        : NsdrFromWork ? NsdrReturn.ResumeWork : NsdrReturn.StartNextPomodoro;

    /// <summary>
    /// What a focus session offers after an NSDR taken inside it: <paramref name="cycle"/> is the
    /// pomodoro state while the NSDR runs, or null for a FREE session.
    /// </summary>
    public static NsdrReturn AfterSessionNsdr(bool inSession, PomodoroCycle? cycle) =>
        !inSession ? NsdrReturn.None
        : cycle is null ? NsdrReturn.ResumeSession
        : cycle.AfterNsdr;

    /// <summary>A work interval finished: count it, then a short break — or a long one after every 4th.</summary>
    public PomodoroCycle CompleteWork()
    {
        if (Phase != PomodoroPhase.Work)
        {
            throw new InvalidOperationException("Only a work interval can be completed.");
        }

        var completed = CompletedWork + 1;
        var next = completed % Pomodoro.IntervalsPerCycle == 0 ? PomodoroPhase.LongBreak : PomodoroPhase.ShortBreak;
        return new PomodoroCycle(next, completed);
    }

    /// <summary>
    /// A break (or NSDR) ended or was skipped: back to work. The count is unchanged. After an NSDR
    /// taken from a work interval this is the same interval again (the caller keeps it paused).
    /// </summary>
    public PomodoroCycle EndBreak()
    {
        if (Phase == PomodoroPhase.Work)
        {
            throw new InvalidOperationException("There is no break to end.");
        }

        return this with { Phase = PomodoroPhase.Work, NsdrFromWork = false };
    }

    /// <summary>Skipping a break is the same transition as it ending.</summary>
    public PomodoroCycle SkipBreak() => EndBreak();

    /// <summary>Swap a long break for a 10-minute NSDR. Only offered in place of a long break.</summary>
    public PomodoroCycle TakeNsdrInstead()
    {
        if (Phase != PomodoroPhase.LongBreak)
        {
            throw new InvalidOperationException("NSDR is offered in place of a long break only.");
        }

        return TakeNsdr();
    }

    /// <summary>
    /// Filled dots in the current cycle of four (●●○○). During the break that follows the 4th
    /// interval all four stay filled; the next work interval starts a fresh, empty cycle.
    /// </summary>
    public int DotsFilled
    {
        get
        {
            var inCycle = CompletedWork % Pomodoro.IntervalsPerCycle;
            return inCycle == 0 && CompletedWork > 0 && IsRestAfterWork ? Pomodoro.IntervalsPerCycle : inCycle;
        }
    }

    /// <summary>The dots as text, e.g. <c>●●○○</c>.</summary>
    public string Dots =>
        new string('●', DotsFilled) + new string('○', Pomodoro.IntervalsPerCycle - DotsFilled);
}
