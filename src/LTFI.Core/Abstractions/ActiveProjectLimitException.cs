using System;

namespace LTFI.Core.Abstractions;

/// <summary>
/// Thrown when activating a project would exceed the active-project limit (plan §3.2). The UI
/// catches this specifically to offer the pause/kill decision instead of a plain error.
/// </summary>
public sealed class ActiveProjectLimitException(int limit)
    : InvalidOperationException(
        $"You already have {limit} active projects. Pause or kill one before activating another.")
{
    public int Limit { get; } = limit;
}
