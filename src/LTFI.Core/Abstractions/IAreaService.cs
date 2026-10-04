using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LTFI.Core.Domain;

namespace LTFI.Core.Abstractions;

/// <summary>Areas sub-divide a project (robotics → firmware / mcad / ecad). Names are unique per project.</summary>
public interface IAreaService
{
    Task<IReadOnlyList<ProjectArea>> GetByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<ProjectArea> CreateAsync(Guid projectId, string name, CancellationToken cancellationToken = default);

    Task RenameAsync(Guid id, string name, CancellationToken cancellationToken = default);

    /// <summary>Removes the area; its tasks stay in the project with no area.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
