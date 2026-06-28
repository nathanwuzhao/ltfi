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
/// Integration tests covering the Phase 1 acceptance criteria against a real SQLite database:
/// create a project, create a task under a project, add subtasks, and survive a restart.
/// </summary>
public class ServiceTests
{
    private static async Task<string> NewMigratedDbAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ltfi-test-{Guid.NewGuid():N}.db");
        await using var db = new TestDbFactory(path).CreateDbContext();
        await db.Database.MigrateAsync();
        return path;
    }

    private static void Cleanup(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-shm", path + "-wal" })
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task CreateProject_persists_and_is_listed()
    {
        var path = await NewMigratedDbAsync();
        try
        {
            var projects = new ProjectService(new TestDbFactory(path));

            var created = await projects.CreateAsync(new ProjectDraft { Title = "Build LTFI" });

            Assert.NotEqual(Guid.Empty, created.Id);
            var all = await projects.GetAllAsync();
            Assert.Single(all);
            Assert.Equal("Build LTFI", all[0].Title);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task CreateTask_under_a_project_links_it()
    {
        var path = await NewMigratedDbAsync();
        try
        {
            var factory = new TestDbFactory(path);
            var projects = new ProjectService(factory);
            var tasks = new TaskService(factory);

            var project = await projects.CreateAsync(new ProjectDraft { Title = "Phase 1" });
            var task = await tasks.CreateAsync(new TaskDraft
            {
                Title = "Define domain model",
                ProjectId = project.Id,
                Priority = TaskPriority.High
            });

            var loaded = await tasks.GetByIdAsync(task.Id);
            Assert.NotNull(loaded);
            Assert.Equal(project.Id, loaded!.ProjectId);
            Assert.Equal(TaskPriority.High, loaded.Priority);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Subtasks_can_be_added_toggled_and_removed()
    {
        var path = await NewMigratedDbAsync();
        try
        {
            var tasks = new TaskService(new TestDbFactory(path));
            var task = await tasks.CreateAsync(new TaskDraft { Title = "Parent task" });

            var first = await tasks.AddSubtaskAsync(task.Id, "Step one");
            await tasks.AddSubtaskAsync(task.Id, "Step two");

            var loaded = await tasks.GetByIdAsync(task.Id);
            Assert.Equal(2, loaded!.Subtasks.Count);
            Assert.Equal(new[] { 0, 1 }, loaded.Subtasks.OrderBy(s => s.SortOrder).Select(s => s.SortOrder));

            await tasks.SetSubtaskCompletedAsync(first.Id, true);
            loaded = await tasks.GetByIdAsync(task.Id);
            Assert.True(loaded!.Subtasks.Single(s => s.Id == first.Id).IsCompleted);

            await tasks.DeleteSubtaskAsync(first.Id);
            loaded = await tasks.GetByIdAsync(task.Id);
            Assert.Single(loaded!.Subtasks);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Data_survives_a_restart()
    {
        var path = await NewMigratedDbAsync();
        try
        {
            // First "session": write data.
            var writeFactory = new TestDbFactory(path);
            var project = await new ProjectService(writeFactory).CreateAsync(new ProjectDraft { Title = "Persisted" });
            await new TaskService(writeFactory).CreateAsync(new TaskDraft { Title = "Persisted task", ProjectId = project.Id });

            // Second "session": a brand-new factory over the same file.
            var readFactory = new TestDbFactory(path);
            var projects = await new ProjectService(readFactory).GetAllAsync();
            var tasks = await new TaskService(readFactory).GetAllAsync();

            Assert.Single(projects);
            Assert.Single(tasks);
            Assert.Equal("Persisted task", tasks[0].Title);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task GetToday_returns_only_open_tasks_due_today_or_earlier()
    {
        var path = await NewMigratedDbAsync();
        try
        {
            var tasks = new TaskService(new TestDbFactory(path));

            var dueToday = await tasks.CreateAsync(new TaskDraft { Title = "Due today", DueAt = DateTimeOffset.Now });
            await tasks.CreateAsync(new TaskDraft { Title = "Due tomorrow", DueAt = DateTimeOffset.Now.AddDays(1) });
            await tasks.CreateAsync(new TaskDraft { Title = "No due date" });
            await tasks.CreateAsync(new TaskDraft
            {
                Title = "Done today",
                DueAt = DateTimeOffset.Now,
                Status = TaskStatus.Completed
            });

            var today = await tasks.GetTodayAsync();

            Assert.Single(today);
            Assert.Equal(dueToday.Id, today[0].Id);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Project_progress_is_derived_from_task_and_subtask_completion()
    {
        var path = await NewMigratedDbAsync();
        try
        {
            var factory = new TestDbFactory(path);
            var projects = new ProjectService(factory);
            var tasks = new TaskService(factory);

            var project = await projects.CreateAsync(new ProjectDraft { Title = "P" });

            // No tasks yet -> null.
            Assert.Null((await projects.GetByIdAsync(project.Id))!.ProgressPercent);

            await tasks.CreateAsync(new TaskDraft { Title = "done", ProjectId = project.Id, Status = TaskStatus.Completed });
            var open = await tasks.CreateAsync(new TaskDraft { Title = "open", ProjectId = project.Id });

            // One of two tasks complete -> 50%.
            Assert.Equal(50, (await projects.GetByIdAsync(project.Id))!.ProgressPercent);

            // The open task earns partial credit from subtasks: 1 of 2 done -> (1.0 + 0.5) / 2 = 75%.
            var sub = await tasks.AddSubtaskAsync(open.Id, "a");
            await tasks.AddSubtaskAsync(open.Id, "b");
            await tasks.SetSubtaskCompletedAsync(sub.Id, true);

            Assert.Equal(75, (await projects.GetByIdAsync(project.Id))!.ProgressPercent);
        }
        finally { Cleanup(path); }
    }
}
