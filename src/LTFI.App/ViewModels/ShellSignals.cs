using System;

namespace LTFI.ViewModels;

/// <summary>
/// App-wide nudges between pages and the shell. Pages call <see cref="NotifyStatsChanged"/> after
/// something that moves the header numbers (points today, streak, active projects): completing a
/// task, keeping a commitment, finishing focus / NSDR, pushing a due date… The shell re-reads its
/// header right away instead of waiting for the 15 s poll.
/// </summary>
public sealed class ShellSignals
{
    public event EventHandler? StatsChanged;

    public void NotifyStatsChanged() => StatsChanged?.Invoke(this, EventArgs.Empty);
}
