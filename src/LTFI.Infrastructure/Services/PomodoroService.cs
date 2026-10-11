using System;
using System.Threading;
using System.Threading.Tasks;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;

namespace LTFI.Infrastructure.Services;

/// <summary>
/// Pomodoro phases layered on the single focus session. Work countdowns are measured against the
/// session's own elapsed (so a manual pause freezes them); breaks run on the wall clock while the
/// session is paused. A long break can be swapped for an NSDR, which <see cref="INsdrService"/> times,
/// and <see cref="TakeNsdrAsync"/> starts one from any work interval / break, or inside a FREE session.
/// Singleton: the phase lives in memory alongside the session's live clock.
/// </summary>
public sealed class PomodoroService(
    IFocusSessionService focus,
    INsdrService nsdr,
    TimeProvider? timeProvider = null) : IPomodoroService
{
    private readonly IFocusSessionService _focus = focus;
    private readonly INsdrService _nsdr = nsdr;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PomodoroCycle? _cycle;
    private Guid? _sessionId;
    private TimeSpan _workStartElapsed;     // session elapsed when the current work interval began
    private DateTimeOffset? _breakStartedAt;
    private bool _breakOver;

    public bool IsActive => CurrentSession() is not null;

    public PomodoroSnapshot? GetSnapshot()
    {
        var session = CurrentSession();
        if (session is null || _cycle is not { } cycle)
        {
            return null;
        }

        TimeSpan remaining;
        switch (cycle.Phase)
        {
            case PomodoroPhase.Work:
                remaining = Pomodoro.WorkDuration - (session.Elapsed - _workStartElapsed);
                break;
            case PomodoroPhase.Nsdr:
                remaining = _breakOver ? TimeSpan.Zero : _nsdr.GetSnapshot()?.Remaining ?? TimeSpan.Zero;
                break;
            default:
                var started = _breakStartedAt ?? _time.GetLocalNow();
                remaining = _breakOver
                    ? TimeSpan.Zero
                    : Pomodoro.DurationOf(cycle.Phase) - (_time.GetLocalNow() - started);
                break;
        }

        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        return new PomodoroSnapshot(
            cycle.Phase,
            remaining,
            cycle.CompletedWork,
            cycle.DotsFilled,
            cycle.Dots,
            _breakOver,
            cycle.Phase == PomodoroPhase.Work && session.Status == FocusSessionStatus.Paused,
            cycle.Phase == PomodoroPhase.Nsdr && cycle.NsdrFromWork);
    }

    public async Task StartAsync(Guid? projectId, Guid? taskId, string? intent, CancellationToken cancellationToken = default)
    {
        var session = await _focus.StartAsync(projectId, taskId, intent, cancellationToken);
        _cycle = PomodoroCycle.Start();
        _sessionId = session.Id;
        _workStartElapsed = TimeSpan.Zero;
        _breakStartedAt = null;
        _breakOver = false;
    }

    public async Task<PomodoroTransition> AdvanceAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return PomodoroTransition.None; // a previous tick is still transitioning
        }

        try
        {
            var session = CurrentSession();
            if (session is null || _cycle is not { } cycle)
            {
                return PomodoroTransition.None;
            }

            switch (cycle.Phase)
            {
                case PomodoroPhase.Work:
                    if (session.Status == FocusSessionStatus.Active
                        && session.Elapsed - _workStartElapsed >= Pomodoro.WorkDuration)
                    {
                        await _focus.CompletePomodoroAsync(cancellationToken);
                        _cycle = cycle.CompleteWork();
                        _breakStartedAt = _time.GetLocalNow();
                        _breakOver = false;
                        return PomodoroTransition.WorkEnded;
                    }

                    return PomodoroTransition.None;

                case PomodoroPhase.Nsdr:
                    if (_breakOver)
                    {
                        return PomodoroTransition.None;
                    }

                    if (await _nsdr.CompleteIfDueAsync(cancellationToken) || !_nsdr.IsRunning)
                    {
                        if (cycle.AfterNsdr == NsdrReturn.ResumeWork)
                        {
                            // Back to the paused work interval; the session clock never ran during
                            // the NSDR, so its remaining time is exactly what it was.
                            _cycle = cycle.EndBreak();
                            return PomodoroTransition.NsdrEnded;
                        }

                        _breakOver = true;
                        return PomodoroTransition.BreakEnded;
                    }

                    return PomodoroTransition.None;

                default:
                    if (!_breakOver && _breakStartedAt is { } started
                        && _time.GetLocalNow() - started >= Pomodoro.DurationOf(cycle.Phase))
                    {
                        _breakOver = true;
                        return PomodoroTransition.BreakEnded;
                    }

                    return PomodoroTransition.None;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SkipBreakAsync(CancellationToken cancellationToken = default) => BeginNextWorkAsync(cancellationToken);

    public Task StartNextAsync(CancellationToken cancellationToken = default) => BeginNextWorkAsync(cancellationToken);

    public void TakeNsdrInstead()
    {
        if (CurrentSession() is null || _cycle is not { Phase: PomodoroPhase.LongBreak } cycle || _breakOver)
        {
            return;
        }

        _cycle = cycle.TakeNsdrInstead();
        _nsdr.Start(_sessionId);
    }

    public async Task<bool> TakeNsdrAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_nsdr.IsRunning)
            {
                return false;
            }

            var session = CurrentSession();
            if (session is not null && _cycle is { } cycle)
            {
                if (!cycle.CanTakeNsdr)
                {
                    return false;
                }

                // Work interval: pause it here (breaks already keep the session paused).
                if (cycle.Phase == PomodoroPhase.Work && session.Status == FocusSessionStatus.Active)
                {
                    await _focus.PauseAsync(cancellationToken);
                }

                _cycle = cycle.TakeNsdr();
                _breakOver = false;
                _nsdr.Start(_sessionId);
                return true;
            }

            // FREE session (no pomodoro run): pause the clock; it stays paused afterwards.
            if (_focus.GetActiveSnapshot() is not { } free)
            {
                return false;
            }

            if (free.Status == FocusSessionStatus.Active)
            {
                await _focus.PauseAsync(cancellationToken);
            }

            _nsdr.Start(free.Id);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Reset()
    {
        if (_cycle is { Phase: PomodoroPhase.Nsdr } && !_breakOver)
        {
            _nsdr.Stop();
        }

        _cycle = null;
        _sessionId = null;
        _workStartElapsed = TimeSpan.Zero;
        _breakStartedAt = null;
        _breakOver = false;
    }

    private async Task BeginNextWorkAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (CurrentSession() is null || _cycle is not { IsBreak: true } cycle)
            {
                return;
            }

            // Skipping an NSDR part-way through records nothing.
            if (cycle.Phase == PomodoroPhase.Nsdr && !_breakOver)
            {
                _nsdr.Stop();
            }

            // An NSDR taken from a work interval returns to that interval, still paused.
            if (cycle.AfterNsdr == NsdrReturn.ResumeWork)
            {
                _cycle = cycle.EndBreak();
                return;
            }

            _cycle = cycle.EndBreak();
            _breakStartedAt = null;
            _breakOver = false;

            await _focus.ResumeAsync(cancellationToken);
            _workStartElapsed = _focus.GetActiveSnapshot()?.Elapsed ?? TimeSpan.Zero;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The active session if it's the one this run belongs to; otherwise forgets the run.</summary>
    private ActiveFocusSnapshot? CurrentSession()
    {
        if (_cycle is null)
        {
            return null;
        }

        var snapshot = _focus.GetActiveSnapshot();
        if (snapshot is null || snapshot.Id != _sessionId)
        {
            Reset();
            return null;
        }

        return snapshot;
    }
}
