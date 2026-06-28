using System;

namespace LTFI.Core.Domain;

/// <summary>
/// A user-defined label that can be attached to tasks (plan §4.1).
/// Defined and persisted in Phase 1; task assignment UI arrives in Phase 2.
/// </summary>
public class TaskLabel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>Hex colour such as <c>#4F8DFD</c>.</summary>
    public string? ColorHex { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
