using System;

namespace LTFI.Core.Domain;

/// <summary>
/// Anti-sprawl policy constants (plan §3.2/§3.4). Kept as constants for now; a Settings page
/// will make them user-configurable in a later phase.
/// </summary>
public static class ProjectPolicy
{
    /// <summary>Maximum number of projects allowed in the Active state at once.</summary>
    public const int MaxActiveProjects = 4;

    /// <summary>An active project with no activity for this many days is considered stalled.</summary>
    public const int StaleAfterDays = 10;

    /// <summary>Target focused hours per week; drives the Command Center "focus debt" meter.</summary>
    public const double WeeklyFocusTargetHours = 15.0;

    // The weekly check-in's open / gate / due times live in CheckInSchedule (settings.json "checkIn").

    /// <summary>Default for how many times per reviewed week the gate may be snoozed before only submitting clears it.</summary>
    public const int MaxCheckInSnoozesPerWeek = 2;

    /// <summary>Default length of one check-in snooze.</summary>
    public const int CheckInSnoozeHours = 3;
}
