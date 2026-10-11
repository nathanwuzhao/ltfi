using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using LTFI.Infrastructure.Services;
using Xunit;
using TaskStatus = LTFI.Core.Domain.TaskStatus;

namespace LTFI.Infrastructure.Tests;

/// <summary>
/// The Focus task picker's project filter (<see cref="FocusTaskPicker"/>) and the shared project
/// colour rule every screen uses (<see cref="ProjectCodes.ColorOrNone"/>), plus the data the UI
/// needs for it (tasks carry their Project; review lines carry the project id).
/// </summary>
public sealed class FocusPickerAndColourTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"ltfi-fp-{Guid.NewGuid():N}");
    private readonly TestDbFactory _factory;

    private static readonly Guid Life = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Boids = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public FocusPickerAndColourTests()
    {
        Directory.CreateDirectory(_folder);
        _factory = new TestDbFactory(Path.Combine(_folder, "test.db"));
        using var db = _factory.CreateDbContext();
        db.Database.Migrate();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    private static TaskItem T(string title, Guid? project, ProjectArea? area = null, TaskStatus status = TaskStatus.Ready) =>
        new() { Title = title, ProjectId = project, Area = area, AreaId = area?.Id, Status = status };

    private static ProjectArea A(string name, int sort) => new() { Name = name, SortOrder = sort, ProjectId = Life };

    [Fact]
    public void NoProject_OffersEveryOpenTask_InIncomingOrder()
    {
        var tasks = new[]
        {
            T("groceries", Life),
            T("flock sim", Boids),
            T("loose", null),
            T("done", Boids, status: TaskStatus.Completed),
            T("dropped", Life, status: TaskStatus.Canceled),
        };

        var titles = FocusTaskPicker.Options(tasks, null).Select(t => t.Title);

        Assert.Equal(["groceries", "flock sim", "loose"], titles);
    }

    [Fact]
    public void Project_OffersOnlyItsOpenTasks_GroupedByArea_NoAreaLast()
    {
        var home = A("home", 1);
        var errands = A("errands", 0);
        var tasks = new[]
        {
            T("no-area 1", Life),
            T("flock sim", Boids),
            T("vacuum", Life, home),
            T("post office", Life, errands),
            T("dishes", Life, home),
            T("done errand", Life, errands, TaskStatus.Completed),
            T("no-area 2", Life),
            T("loose", null),
        };

        var titles = FocusTaskPicker.Options(tasks, Life).Select(t => t.Title);

        // errands (sort 0) → home (sort 1, incoming order kept) → no area (incoming order kept)
        Assert.Equal(["post office", "vacuum", "dishes", "no-area 1", "no-area 2"], titles);
        Assert.Equal(["flock sim"], FocusTaskPicker.Options(tasks, Boids).Select(t => t.Title));
    }

    [Fact]
    public void Project_AreasWithTheSameSortOrder_FallBackToName()
    {
        var tasks = new[] { T("z", Life, A("zeta", 0)), T("a", Life, A("Alpha", 0)) };

        Assert.Equal(["a", "z"], FocusTaskPicker.Options(tasks, Life).Select(t => t.Title));
    }

    [Fact]
    public void Belongs_KeepsATaskOnlyUnderItsOwnProject()
    {
        Assert.True(FocusTaskPicker.Belongs(Life, null));
        Assert.True(FocusTaskPicker.Belongs(null, null));
        Assert.True(FocusTaskPicker.Belongs(Life, Life));
        Assert.False(FocusTaskPicker.Belongs(Boids, Life));
        Assert.False(FocusTaskPicker.Belongs(null, Life));
    }

    [Fact]
    public void ColorOrNone_IsTheOneRule_ProjectStandingNone()
    {
        Assert.Equal(ProjectCodes.ColorFor(Boids), ProjectCodes.ColorOrNone(Boids));
        Assert.Equal(ProjectCodes.StandingColor, ProjectCodes.ColorOrNone(Life, isStanding: true));
        Assert.Equal(ProjectCodes.NoneColor, ProjectCodes.ColorOrNone(null));
        Assert.Equal(ProjectCodes.NoneColor, ProjectCodes.ColorOrNone(null, isStanding: true));

        // "No project" reads as absence: not a palette colour, not the standing grey, not a status colour.
        Assert.DoesNotContain(ProjectCodes.NoneColor, ProjectCodes.Palette);
        Assert.NotEqual(ProjectCodes.StandingColor, ProjectCodes.NoneColor);
        Assert.DoesNotContain(ProjectCodes.NoneColor, new[] { "#46D17F", "#E6A13A", "#E5484D" });
    }

    [Fact]
    public async Task Tasks_CarryTheirProject_ForTheColourTag()
    {
        var projects = new ProjectService(_factory);
        var tasks = new TaskService(_factory);
        var boids = await projects.CreateAsync(new ProjectDraft { Title = "boids 01" });
        var task = await tasks.CreateAsync(new TaskDraft { Title = "flock sim", ProjectId = boids.Id, DueAt = DateTimeOffset.Now });
        await tasks.CreateAsync(new TaskDraft { Title = "loose" });

        var all = await tasks.GetAllAsync();
        var loaded = all.Single(t => t.Id == task.Id);
        Assert.NotNull(loaded.Project);
        Assert.Equal(boids.Id, loaded.Project!.Id);
        Assert.Equal("BOID", ProjectCodes.Code(loaded.Project.Title));
        Assert.Null(all.Single(t => t.Title == "loose").Project);

        var today = await tasks.GetTodayAsync();
        Assert.Equal("boids 01", today.Single(t => t.Id == task.Id).Project?.Title);
    }

    [Fact]
    public async Task ReviewLines_CarryTheProjectId_ForTheColour()
    {
        var projects = new ProjectService(_factory);
        var life = await projects.CreateAsync(new ProjectDraft { Title = "Life", IsStanding = true });
        var boids = await projects.CreateAsync(new ProjectDraft { Title = "boids 01" });

        var r = await new ReviewService(_factory).GetWeeklyReviewAsync();

        var lifeLine = r.ProjectActivity.Single(l => l.Title == "Life");
        Assert.Equal(life.Id, lifeLine.ProjectId);
        Assert.True(lifeLine.IsStanding);
        var boidsLine = r.ProjectActivity.Single(l => l.Title == "boids 01");
        Assert.Equal(boids.Id, boidsLine.ProjectId);
        Assert.False(boidsLine.IsStanding);
    }
}
