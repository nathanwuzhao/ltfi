using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using LTFI.Infrastructure.Persistence;

namespace LTFI.Infrastructure.Services;

public sealed class MilestoneService(IDbContextFactory<LtfiDbContext> contextFactory) : IMilestoneService
{
    private readonly IDbContextFactory<LtfiDbContext> _contextFactory = contextFactory;

    public async Task<IReadOnlyList<Milestone>> GetByProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Milestones
            .AsNoTracking()
            .Where(m => m.ProjectId == projectId)
            .OrderBy(m => m.SortOrder)
            .ToListAsync(cancellationToken);
    }

    public async Task<Milestone> CreateAsync(Guid projectId, MilestoneDraft draft, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(draft.Title))
        {
            throw new InvalidOperationException("Milestone title is required.");
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, cancellationToken))
        {
            throw new InvalidOperationException("Project could not be found.");
        }

        var nextOrder = await db.Milestones.CountAsync(m => m.ProjectId == projectId, cancellationToken);
        var milestone = new Milestone
        {
            ProjectId = projectId,
            Title = draft.Title.Trim(),
            Description = Normalize(draft.Description),
            Status = draft.Status,
            TargetDate = draft.TargetDate,
            SortOrder = nextOrder
        };

        db.Milestones.Add(milestone);
        await db.SaveChangesAsync(cancellationToken);
        return milestone;
    }

    public async Task UpdateAsync(Guid id, MilestoneDraft draft, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(draft.Title))
        {
            throw new InvalidOperationException("Milestone title is required.");
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var milestone = await db.Milestones.FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Milestone could not be found.");

        milestone.Title = draft.Title.Trim();
        milestone.Description = Normalize(draft.Description);
        milestone.Status = draft.Status;
        milestone.TargetDate = draft.TargetDate;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetStatusAsync(Guid id, MilestoneStatus status, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var milestone = await db.Milestones.FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Milestone could not be found.");

        milestone.Status = status;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var milestone = await db.Milestones.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (milestone is null)
        {
            return;
        }

        db.Milestones.Remove(milestone);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
