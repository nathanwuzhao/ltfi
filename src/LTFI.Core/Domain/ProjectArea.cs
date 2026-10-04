using System;
using System.Collections.Generic;

namespace LTFI.Core.Domain;

/// <summary>
/// A sub-division within a project (e.g. robotics → firmware / mcad / ecad). For the standing
/// project ("Life") each area mirrors one iCloud Reminders list, so the area name doubles as the
/// list name when LTFI creates a reminder there.
/// </summary>
public class ProjectArea
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<TaskItem> Tasks { get; set; } = new List<TaskItem>();
}
