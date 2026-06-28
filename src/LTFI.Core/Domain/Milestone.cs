using System;

namespace LTFI.Core.Domain;

/// <summary>A checkpoint within a project (plan §3.3). Persisted now; managed in Phase 3.</summary>
public class Milestone
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public MilestoneStatus Status { get; set; } = MilestoneStatus.Planned;

    public DateTimeOffset? TargetDate { get; set; }

    public int SortOrder { get; set; }
}
