using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LTFI.Core.Domain;

namespace LTFI.Core.Abstractions;

/// <summary>A full export of the user's reminders at one point in time.</summary>
public sealed record ReminderSnapshot(
    IReadOnlyList<ExternalReminder> Reminders,
    DateTimeOffset? ExportedAt,
    string? Producer);

/// <summary>
/// Cheap availability check for a source. <see cref="Version"/> is an opaque change token
/// (e.g. file mtime + size) the sync uses to skip unchanged snapshots; null when unavailable.
/// </summary>
public sealed record ReminderSourceProbe(bool IsAvailable, string Location, string? Version);

/// <summary>Thrown when a source exists but its snapshot can't be read or parsed.</summary>
public sealed class ReminderSourceException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Produces reminder snapshots from somewhere outside LTFI. The first implementation reads a JSON
/// file an iPhone Shortcut drops into iCloud Drive; a pyicloud sidecar (or anything else that emits
/// the same <c>ltfi.reminders/v1</c> JSON) can be swapped in without touching the sync logic.
/// </summary>
public interface IReminderSource
{
    ReminderSourceProbe Probe();

    /// <summary>Reads the current snapshot. Throws <see cref="ReminderSourceException"/> when the
    /// source is missing, unreadable, or malformed.</summary>
    Task<ReminderSnapshot> ReadAsync(CancellationToken cancellationToken = default);
}
