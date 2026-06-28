using System;

namespace LTFI.Core.Domain;

/// <summary>
/// A concrete signal that work happened (plan §2.2, §4.4, §5) — the backbone of
/// "evidence over vibes". Persisted in Phase 1; the timeline view is built in Phase 4.
/// </summary>
public class EvidenceItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? ProjectId { get; set; }

    public Guid? TaskId { get; set; }

    public Guid? FocusSessionId { get; set; }

    public EvidenceType Type { get; set; }

    /// <summary>Where the evidence came from, e.g. "manual", "git", "logseq".</summary>
    public string Source { get; set; } = "manual";

    public string Title { get; set; } = string.Empty;

    public string? Summary { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Free-form JSON for type-specific detail; kept opaque to the domain.</summary>
    public string? MetadataJson { get; set; }
}
