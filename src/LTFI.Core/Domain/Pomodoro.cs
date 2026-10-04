using System;

namespace LTFI.Core.Domain;

/// <summary>How the Focus page times a session: pomodoro countdown intervals, or a free count-up.</summary>
public enum FocusTimerMode
{
    Pomodoro,
    Free
}

/// <summary>The phase a pomodoro run is in. <see cref="Nsdr"/> replaces a long break when chosen.</summary>
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

/// <summary>
/// The pure pomodoro state machine: which phase a run is in and how many work intervals it has
/// completed. Immutable — each transition returns the next state. Timing is the caller's job.
/// </summary>
public sealed record PomodoroCycle(PomodoroPhase Phase, int CompletedWork)
{
    /// <summary>A fresh run: first work interval, nothing completed.</summary>
    public static PomodoroCycle Start() => new(PomodoroPhase.Work, 0);

    public bool IsBreak => Phase != PomodoroPhase.Work;

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

    /// <summary>A break (or NSDR) ended or was skipped: back to work. The count is unchanged.</summary>
    public PomodoroCycle EndBreak()
    {
        if (Phase == PomodoroPhase.Work)
        {
            throw new InvalidOperationException("There is no break to end.");
        }

        return this with { Phase = PomodoroPhase.Work };
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

        return this with { Phase = PomodoroPhase.Nsdr };
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
            return inCycle == 0 && CompletedWork > 0 && IsBreak ? Pomodoro.IntervalsPerCycle : inCycle;
        }
    }

    /// <summary>The dots as text, e.g. <c>●●○○</c>.</summary>
    public string Dots =>
        new string('●', DotsFilled) + new string('○', Pomodoro.IntervalsPerCycle - DotsFilled);
}
