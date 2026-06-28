using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LTFI.Core.Domain;

namespace LTFI.Core.Abstractions;

/// <summary>CRUD operations for projects. Implemented over persistence in Infrastructure.</summary>
public interface IProjectService
{
    Task<IReadOnlyList<Project>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Number of projects currently in the Active state.</summary>
    Task<int> CountActiveAsync(CancellationToken cancellationToken = default);

    Task<Project> CreateAsync(ProjectDraft draft, CancellationToken cancellationToken = default);

    Task UpdateAsync(Guid id, ProjectDraft draft, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
