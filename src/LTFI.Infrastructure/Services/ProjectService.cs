using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using LTFI.Infrastructure.Persistence;

namespace LTFI.Infrastructure.Services;

/// <summary>Persistence-backed <see cref="IProjectService"/>. A fresh context is used per operation.</summary>
public sealed class ProjectService(IDbContextFactory<LtfiDbContext> contextFactory) : IProjectService
{
    private readonly IDbContextFactory<LtfiDbContext> _contextFactory = contextFactory;

    public async Task<IReadOnlyList<Project>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        // Tasks + subtasks are loaded so Project.ProgressPercent can be computed.
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Projects
            .AsNoTracking()
            .AsSplitQuery()
            .Include(p => p.Tasks)
            .ThenInclude(t => t.Subtasks)
            .OrderBy(p => p.Title)
            .ToListAsync(cancellationToken);
    }

    public async Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Projects
            .AsNoTracking()
            .AsSplitQuery()
            .Include(p => p.Tasks)
            .ThenInclude(t => t.Subtasks)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    public async Task<Project> CreateAsync(ProjectDraft draft, CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft);

        var now = DateTimeOffset.Now;
        var project = new Project
        {
            Title = draft.Title.Trim(),
            Description = Normalize(draft.Description),
            Status = draft.Status,
            DoneCondition = Normalize(draft.DoneCondition),
            TargetDate = draft.TargetDate,
            CreatedAt = now,
            UpdatedAt = now
        };
        ApplyArchiveState(project);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        db.Projects.Add(project);
        await db.SaveChangesAsync(cancellationToken);
        return project;
    }

    public async Task UpdateAsync(Guid id, ProjectDraft draft, CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Project could not be found.");

        // A killed project is intentionally ended; it can't be reactivated (timed reactivation is Phase 3).
        if (project.Status == ProjectStatus.Killed && draft.Status != ProjectStatus.Killed)
        {
            throw new InvalidOperationException("This project was killed and can't be reactivated.");
        }

        project.Title = draft.Title.Trim();
        project.Description = Normalize(draft.Description);
        project.Status = draft.Status;
        project.DoneCondition = Normalize(draft.DoneCondition);
        project.TargetDate = draft.TargetDate;
        project.UpdatedAt = DateTimeOffset.Now;
        ApplyArchiveState(project);

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Stamps ArchivedAt when a project becomes Completed/Killed, clears it otherwise.</summary>
    private static void ApplyArchiveState(Project project)
    {
        project.ArchivedAt = project.IsArchived
            ? project.ArchivedAt ?? DateTimeOffset.Now
            : null;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (project is null)
        {
            return;
        }

        db.Projects.Remove(project);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void ValidateDraft(ProjectDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Title))
        {
            throw new InvalidOperationException("Project title is required.");
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
