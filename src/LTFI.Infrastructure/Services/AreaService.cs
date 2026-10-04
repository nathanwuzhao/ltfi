using Microsoft.EntityFrameworkCore;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using LTFI.Infrastructure.Persistence;

namespace LTFI.Infrastructure.Services;

/// <summary>Persistence-backed <see cref="IAreaService"/>. Area names are unique (case-insensitive) per project.</summary>
public sealed class AreaService(IDbContextFactory<LtfiDbContext> contextFactory) : IAreaService
{
    private readonly IDbContextFactory<LtfiDbContext> _contextFactory = contextFactory;

    public async Task<IReadOnlyList<ProjectArea>> GetByProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Areas
            .AsNoTracking()
            .Where(a => a.ProjectId == projectId)
            .OrderBy(a => a.SortOrder)
            .ThenBy(a => a.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<ProjectArea> CreateAsync(Guid projectId, string name, CancellationToken cancellationToken = default)
    {
        var trimmed = ValidateName(name);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, cancellationToken))
        {
            throw new InvalidOperationException("Project could not be found.");
        }

        var siblings = await db.Areas.Where(a => a.ProjectId == projectId).ToListAsync(cancellationToken);
        EnsureUnique(siblings, trimmed, Guid.Empty);

        var area = new ProjectArea
        {
            ProjectId = projectId,
            Name = trimmed,
            SortOrder = siblings.Count == 0 ? 0 : siblings.Max(a => a.SortOrder) + 1,
            CreatedAt = DateTimeOffset.Now
        };

        db.Areas.Add(area);
        await db.SaveChangesAsync(cancellationToken);
        return area;
    }

    public async Task RenameAsync(Guid id, string name, CancellationToken cancellationToken = default)
    {
        var trimmed = ValidateName(name);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var area = await db.Areas.FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Area could not be found.");

        var siblings = await db.Areas.Where(a => a.ProjectId == area.ProjectId).ToListAsync(cancellationToken);
        EnsureUnique(siblings, trimmed, id);

        area.Name = trimmed;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var area = await db.Areas.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (area is null)
        {
            return;
        }

        // Load the area's tasks so EF nulls their AreaId even if the FK action isn't applied.
        await db.Tasks.Where(t => t.AreaId == id).LoadAsync(cancellationToken);
        db.Areas.Remove(area);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Area name is required.");
        }

        return name.Trim();
    }

    private static void EnsureUnique(IEnumerable<ProjectArea> siblings, string name, Guid exceptId)
    {
        if (siblings.Any(a => a.Id != exceptId && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"This project already has an area called \"{name}\".");
        }
    }
}
