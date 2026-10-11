using System;

namespace LTFI.ViewModels;

/// <summary>
/// What a page asks the shell to open: a task (Tasks page) or, with no task, a project (Projects
/// page). <see cref="StartFocus"/> opens the Focus page instead, its session form pre-filled for
/// the task/project — ready to press Start, never auto-started.
/// </summary>
public sealed record OpenItemRequest(Guid? TaskId, Guid? ProjectId, bool StartFocus);

/// <summary>
/// App-wide nudges between pages and the shell. Pages call <see cref="NotifyStatsChanged"/> after
/// something that moves the header numbers (points today, streak, active projects): completing a
/// task, keeping a commitment, finishing focus / NSDR, pushing a due date… The shell re-reads its
/// header right away instead of waiting for the 15 s poll. <see cref="RequestOpenItem"/> asks the
/// shell to jump to a task or project (a Command Center row was clicked).
/// </summary>
public sealed class ShellSignals
{
    public event EventHandler? StatsChanged;

    public event EventHandler<OpenItemRequest>? OpenItemRequested;

    public void NotifyStatsChanged() => StatsChanged?.Invoke(this, EventArgs.Empty);

    public void RequestOpenItem(OpenItemRequest request) => OpenItemRequested?.Invoke(this, request);
}
