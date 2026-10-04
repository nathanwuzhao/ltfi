using System.Threading;
using System.Threading.Tasks;

namespace LTFI.Core.Abstractions;

/// <summary>
/// LTFI → iPhone write-back. Pending <see cref="Domain.OutboxCommand"/> rows are written to
/// <c>outbox.json</c> next to the reminders export, where the "LTFI Apply" Shortcut picks them up.
/// </summary>
public interface IReminderOutbox
{
    /// <summary>Where <c>outbox.json</c> is written.</summary>
    string Location { get; }

    /// <summary>The last write failure (folder missing, IO error); null after a good write.</summary>
    string? LastError { get; }

    /// <summary>Commands not yet confirmed by an export.</summary>
    Task<int> CountPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>Rewrites <c>outbox.json</c> (atomically) with every unconfirmed command. Never throws
    /// for file problems; returns false and sets <see cref="LastError"/> instead.</summary>
    Task<bool> FlushAsync(CancellationToken cancellationToken = default);
}
