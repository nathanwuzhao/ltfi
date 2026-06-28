using System;

namespace LTFI.Core.Domain;

/// <summary>
/// A written reflection scoped to a day, week, project, or task (plan §5).
/// Persisted in Phase 1; structured daily/weekly review is built in Phases 3 and 5.
/// </summary>
public class ReflectionEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public ReflectionScope ScopeType { get; set; }

    /// <summary>Id of the scoped project/task when applicable; null for day/week scopes.</summary>
    public Guid? ScopeId { get; set; }

    public string? Prompt { get; set; }

    public string Body { get; set; } = string.Empty;

    /// <summary>Optional LLM/structured summary as JSON; user-confirmed before use (plan §2.4).</summary>
    public string? StructuredSummaryJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
