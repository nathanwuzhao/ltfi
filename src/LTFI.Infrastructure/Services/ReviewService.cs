using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using LTFI.Infrastructure.Persistence;
using TaskStatus = LTFI.Core.Domain.TaskStatus;

namespace LTFI.Infrastructure.Services;

/// <summary>
/// Builds the weekly review and stalled-project list from local data (plan §3.6). All windowing
/// is done in memory because SQLite can't translate DateTimeOffset comparisons.
/// </summary>
public sealed class ReviewService(IDbContextFactory<LtfiDbContext> contextFactory) : IReviewService
{
    private readonly IDbContextFactory<LtfiDbContext> _contextFactory = contextFactory;

    public async Task<WeeklyReview> GetWeeklyReviewAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.Now;
        var weekAgo = now.AddDays(-7);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var projects = await db.Projects.AsNoTracking()
            .Select(p => new { p.Id, p.Title, p.Status, p.CreatedAt, p.ArchivedAt, p.LastActiveAt })
            .ToListAsync(cancellationToken);

        var tasks = await db.Tasks.AsNoTracking()
            .Select(t => new { t.ProjectId, t.Status, t.CompletedAt })
            .ToListAsync(cancellationToken);

        var sessions = await db.FocusSessions.AsNoTracking()
            .Where(s => s.Status == FocusSessionStatus.Completed)
            .Select(s => new { s.ProjectId, s.Duration, s.EndedAt })
            .ToListAsync(cancellationToken);

        var evidence = await db.Evidence.AsNoTracking()
            .Select(e => new { e.ProjectId, e.OccurredAt })
            .ToListAsync(cancellationToken);

        var completedThisWeek = tasks
            .Where(t => t.Status == TaskStatus.Completed && t.CompletedAt >= weekAgo)
            .ToList();

        var sessionsThisWeek = sessions.Where(s => s.EndedAt >= weekAgo).ToList();
        var activeProjects = projects.Where(p => p.Status == ProjectStatus.Active).ToList();

        var projectActivity = activeProjects
            .Select(p => new ProjectActivityLine(
                p.Title,
                Sum(sessionsThisWeek.Where(s => s.ProjectId == p.Id).Select(s => s.Duration)),
                completedThisWeek.Count(t => t.ProjectId == p.Id)))
            .OrderByDescending(l => l.FocusTime)
            .ToList();

        var stalled = new List<StalledProjectLine>();
        foreach (var p in activeProjects)
        {
            var times = new List<DateTimeOffset> { p.LastActiveAt ?? p.CreatedAt };
            times.AddRange(sessions.Where(s => s.ProjectId == p.Id && s.EndedAt is not null).Select(s => s.EndedAt!.Value));
            times.AddRange(evidence.Where(e => e.ProjectId == p.Id).Select(e => e.OccurredAt));
            times.AddRange(tasks.Where(t => t.ProjectId == p.Id && t.CompletedAt is not null).Select(t => t.CompletedAt!.Value));

            var days = (int)(now - times.Max()).TotalDays;
            if (days > ProjectPolicy.StaleAfterDays)
            {
                stalled.Add(new StalledProjectLine(p.Title, days));
            }
        }

        return new WeeklyReview(
            ActiveProjectCount: activeProjects.Count,
            MaxActiveProjects: ProjectPolicy.MaxActiveProjects,
            IsOverLimit: activeProjects.Count > ProjectPolicy.MaxActiveProjects,
            TasksCompletedThisWeek: completedThisWeek.Count,
            FocusTimeThisWeek: Sum(sessionsThisWeek.Select(s => s.Duration)),
            NewProjectsThisWeek: projects.Count(p => p.CreatedAt >= weekAgo),
            ArchivedProjectsThisWeek: projects.Count(p => p.ArchivedAt >= weekAgo),
            ProjectActivity: projectActivity,
            StalledProjects: stalled.OrderByDescending(s => s.DaysSinceActivity).ToList());
    }

    private static TimeSpan Sum(IEnumerable<TimeSpan?> durations) =>
        durations.Aggregate(TimeSpan.Zero, (sum, d) => sum + (d ?? TimeSpan.Zero));
}
