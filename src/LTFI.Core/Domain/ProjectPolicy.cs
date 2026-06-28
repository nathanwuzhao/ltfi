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
}
