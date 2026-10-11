using System;

namespace LTFI.Core.Domain;

/// <summary>
/// When the weekly check-in for a week opens, starts gating the app, and falls due. A week runs
/// Monday 00:00 → Sunday 23:59:59 local; a check-in <em>reviews</em> the week that is ending.
/// Every moment is an offset from that week's Monday 00:00 (wall clock), so "Saturday 00:00" is
/// 5 days. <see cref="Due"/> is the last minute that still counts as on time (Sunday 23:59 means
/// the deadline is Monday 00:00).
/// </summary>
public sealed record CheckInSchedule
{
    /// <summary>The longest possible week offset: the check-in is due no later than Sunday 23:59.</summary>
    private static readonly TimeSpan LastMinute = TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1);

    public CheckInSchedule(TimeSpan opens, TimeSpan gateFrom, TimeSpan due, int maxSnoozes, int snoozeHours)
    {
        if (opens < TimeSpan.Zero || opens > gateFrom || gateFrom > due || due > LastMinute)
        {
            throw new ArgumentException(
                $"Check-in times must be in week order (opens ≤ gate ≤ due ≤ Sunday 23:59): opens {opens}, gate {gateFrom}, due {due}.");
        }

        if (maxSnoozes < 0 || snoozeHours < 1)
        {
            throw new ArgumentException("Snoozes must be ≥ 0 and snooze hours ≥ 1.");
        }

        Opens = opens;
        GateFrom = gateFrom;
        Due = due;
        MaxSnoozes = maxSnoozes;
        SnoozeHours = snoozeHours;
    }

    /// <summary>The window opens (form available, header chip shows) — default Saturday 00:00.</summary>
    public TimeSpan Opens { get; }

    /// <summary>From here until submitted the app is gated (snoozable) — default Sunday 18:00.</summary>
    public TimeSpan GateFrom { get; }

    /// <summary>The last on-time minute — default Sunday 23:59.</summary>
    public TimeSpan Due { get; }

    /// <summary>The first overdue moment (one minute after <see cref="Due"/>): Monday 00:00 by default.</summary>
    public TimeSpan Deadline => Due + TimeSpan.FromMinutes(1);

    /// <summary>Snoozes allowed per reviewed week.</summary>
    public int MaxSnoozes { get; }

    public int SnoozeHours { get; }

    /// <summary>Saturday 00:00 open, Sunday 18:00 gate, Sunday 23:59 due, 2 snoozes × 3h.</summary>
    public static CheckInSchedule Default { get; } = new(
        At(DayOfWeek.Saturday, 0, 0),
        At(DayOfWeek.Sunday, 18, 0),
        At(DayOfWeek.Sunday, 23, 59),
        ProjectPolicy.MaxCheckInSnoozesPerWeek,
        ProjectPolicy.CheckInSnoozeHours);

    /// <summary>A day + time as an offset from Monday 00:00 (Monday = 0 … Sunday = 6 days).</summary>
    public static TimeSpan At(DayOfWeek day, int hour, int minute) =>
        TimeSpan.FromDays(DaysFromMonday(day)) + new TimeSpan(hour, minute, 0);

    public static int DaysFromMonday(DayOfWeek day) => ((int)day + 6) % 7;
}

/// <summary>Where the check-in for the current review week stands.</summary>
public enum CheckInPhase
{
    /// <summary>The review week already has a check-in (or nothing is open yet); no form, no gate.</summary>
    Done,

    /// <summary>Window open, before the gate time: form available, amber header chip, no gate.</summary>
    Open,

    /// <summary>Gate time → deadline, not submitted: gated (snoozable), amber chip.</summary>
    Closing,

    /// <summary>The deadline passed without a check-in: gated (snoozable), red chip.</summary>
    Overdue
}
