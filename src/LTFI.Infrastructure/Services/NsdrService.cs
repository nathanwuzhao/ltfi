using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using LTFI.Infrastructure.Persistence;

namespace LTFI.Infrastructure.Services;

/// <summary>
/// Holds the single running NSDR in memory (a singleton, so it survives navigation). Wall-clock
/// timed; there is no pause — stopping early simply discards the run and records nothing.
/// </summary>
public sealed class NsdrService(
    IDbContextFactory<LtfiDbContext> contextFactory,
    TimeProvider? timeProvider = null) : INsdrService
{
    private readonly IDbContextFactory<LtfiDbContext> _contextFactory = contextFactory;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();

    private DateTimeOffset? _startedAt;
    private Guid? _focusSessionId;

    public bool IsRunning
    {
        get { lock (_gate) { return _startedAt is not null; } }
    }

    public NsdrSnapshot? GetSnapshot()
    {
        lock (_gate)
        {
            if (_startedAt is not { } started)
            {
                return null;
            }

            var elapsed = _time.GetLocalNow() - started;
            if (elapsed > Nsdr.Duration)
            {
                elapsed = Nsdr.Duration;
            }

            var remaining = Nsdr.Duration - elapsed;
            var index = Nsdr.CueIndexAt(elapsed);
            var next = index + 1 < Nsdr.Cues.Count ? Nsdr.Cues[index + 1] : null;
            return new NsdrSnapshot(elapsed, remaining, index, Nsdr.Cues[index], next, _focusSessionId);
        }
    }

    public void Start(Guid? focusSessionId = null)
    {
        lock (_gate)
        {
            _startedAt = _time.GetLocalNow();
            _focusSessionId = focusSessionId;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _startedAt = null;
            _focusSessionId = null;
        }
    }

    public async Task<bool> CompleteIfDueAsync(CancellationToken cancellationToken = default)
    {
        Guid? sessionId;
        DateTimeOffset completedAt;
        lock (_gate)
        {
            if (_startedAt is not { } started || _time.GetLocalNow() - started < Nsdr.Duration)
            {
                return false;
            }

            // Claim the completion before the await so a concurrent tick can't record it twice.
            sessionId = _focusSessionId;
            completedAt = started + Nsdr.Duration;
            _startedAt = null;
            _focusSessionId = null;
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Only link the session if its row exists (the FK must resolve).
        if (sessionId is { } sid && !await db.FocusSessions.AnyAsync(s => s.Id == sid, cancellationToken))
        {
            sessionId = null;
        }

        db.Evidence.Add(new EvidenceItem
        {
            Type = EvidenceType.NsdrCompleted,
            Source = "focus",
            Title = "NSDR · 10 min",
            FocusSessionId = sessionId,
            OccurredAt = completedAt
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
