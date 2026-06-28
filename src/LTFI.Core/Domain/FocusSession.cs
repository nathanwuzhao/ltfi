using System;

namespace LTFI.Core.Domain;

/// <summary>
/// A timed block of focused work against a project/task (plan §2.2).
/// Persisted in Phase 1 so the schema is stable; the timer UI is built in Phase 2.
/// </summary>
public class FocusSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? ProjectId { get; set; }

    public Guid? TaskId { get; set; }

    public string? Intent { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public TimeSpan? Duration { get; set; }

    public FocusSessionStatus Status { get; set; } = FocusSessionStatus.Active;

    /// <summary>The review outcome recorded at finish (plan §2.3); null while running or if abandoned.</summary>
    public FocusSessionResult? Result { get; set; }

    public string? ResultSummary { get; set; }

    public string? BlockerSummary { get; set; }

    public string? NextAction { get; set; }
}
