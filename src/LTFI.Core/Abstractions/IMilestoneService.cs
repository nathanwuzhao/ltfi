using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LTFI.Core.Domain;

namespace LTFI.Core.Abstractions;

/// <summary>Input for creating or updating a <see cref="Milestone"/>.</summary>
public sealed record MilestoneDraft
{
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public MilestoneStatus Status { get; init; } = MilestoneStatus.Planned;
    public DateTimeOffset? TargetDate { get; init; }
}

/// <summary>Milestones belong to a project and order its major checkpoints (plan §3.3).</summary>
public interface IMilestoneService
{
    Task<IReadOnlyList<Milestone>> GetByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<Milestone> CreateAsync(Guid projectId, MilestoneDraft draft, CancellationToken cancellationToken = default);

    Task UpdateAsync(Guid id, MilestoneDraft draft, CancellationToken cancellationToken = default);

    Task SetStatusAsync(Guid id, MilestoneStatus status, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
