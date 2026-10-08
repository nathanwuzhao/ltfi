using System;
using System.Linq;
using LTFI.Core.Domain;
using Xunit;
using TaskStatus = LTFI.Core.Domain.TaskStatus;

namespace LTFI.Infrastructure.Tests;

public class TaskAgendaTests
{
    private static readonly DateOnly Today = new(2026, 10, 7);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static DateTimeOffset Day(int offsetDays, int hour = 0) =>
        new(Today.AddDays(offsetDays).ToDateTime(new TimeOnly(hour, 0)), TimeSpan.Zero);

    private static TaskItem Make(string title, int? dueOffset = null,
        TaskPriority priority = TaskPriority.Medium, TaskStatus status = TaskStatus.Ready, int hour = 0) =>
        new()
        {
            Title = title,
            DueAt = dueOffset is { } d ? Day(d, hour) : null,
            Priority = priority,
            Status = status
        };

    [Theory]
    [InlineData(-30, DueBucket.Overdue)]
    [InlineData(-1, DueBucket.Overdue)]
    [InlineData(0, DueBucket.Today)]
    [InlineData(1, DueBucket.Tomorrow)]
    [InlineData(2, DueBucket.ThisWeek)]
    [InlineData(7, DueBucket.ThisWeek)]
    [InlineData(8, DueBucket.Later)]
    [InlineData(60, DueBucket.Later)]
    public void Buckets_follow_local_calendar_days(int offset, DueBucket expected)
    {
        Assert.Equal(expected, TaskAgenda.BucketOf(Day(offset), Today, Utc));
        Assert.Equal(expected, TaskAgenda.BucketOf(Day(offset, 23), Today, Utc));
    }

    [Fact]
    public void No_due_date_is_its_own_bucket() =>
        Assert.Equal(DueBucket.NoDate, TaskAgenda.BucketOf(null, Today, Utc));

    [Fact]
    public void Bucket_uses_the_given_zone_not_the_offset_stored_on_the_value()
    {
        // 2026-10-08 02:00 UTC is still 2026-10-07 in New York (UTC-4): today, not tomorrow.
        var due = new DateTimeOffset(2026, 10, 8, 2, 0, 0, TimeSpan.Zero);
        var ny = TimeZoneInfo.CreateCustomTimeZone("ny-test", TimeSpan.FromHours(-4), "ny", "ny");
        Assert.Equal(DueBucket.Today, TaskAgenda.BucketOf(due, Today, ny));
        Assert.Equal(DueBucket.Tomorrow, TaskAgenda.BucketOf(due, Today, Utc));
    }

    [Fact]
    public void Due_grouping_orders_sections_and_hides_empty_ones()
    {
        var tasks = new[]
        {
            Make("later", 20), Make("none"), Make("yesterday", -1), Make("today", 0), Make("week", 7)
        };

        var groups = TaskAgenda.Build(tasks, Today, TaskGrouping.Due, TaskSort.Due, Utc);

        Assert.Equal(new[] { "OVERDUE", "TODAY", "THIS WEEK", "LATER", "NO DUE DATE" },
            groups.Select(g => g.Label));
        Assert.True(groups[0].IsAlert);
        Assert.All(groups.Skip(1), g => Assert.False(g.IsAlert));
        Assert.DoesNotContain(groups, g => g.Label == "TOMORROW");
    }

    [Fact]
    public void Within_a_section_due_sort_is_date_then_priority_then_title()
    {
        var tasks = new[]
        {
            Make("b-low", 3, TaskPriority.Low),
            Make("z-urgent", 3, TaskPriority.Urgent),
            Make("a-urgent", 3, TaskPriority.Urgent),
            Make("earlier", 2, TaskPriority.Low),
        };

        var week = Assert.Single(TaskAgenda.Build(tasks, Today, TaskGrouping.Due, TaskSort.Due, Utc));

        Assert.Equal(new[] { "earlier", "a-urgent", "z-urgent", "b-low" }, week.Tasks.Select(t => t.Title));
    }

    [Fact]
    public void Priority_sort_is_urgent_first_then_due_then_title()
    {
        var tasks = new[]
        {
            Make("low", 0, TaskPriority.Low),
            Make("high-none", null, TaskPriority.High),
            Make("high-soon", 1, TaskPriority.High),
            Make("urgent", 9, TaskPriority.Urgent),
        };

        var flat = Assert.Single(TaskAgenda.Build(tasks, Today, TaskGrouping.None, TaskSort.Priority, Utc));

        Assert.Equal(new[] { "urgent", "high-soon", "high-none", "low" }, flat.Tasks.Select(t => t.Title));
    }

    [Fact]
    public void Title_sort_is_case_insensitive()
    {
        var tasks = new[] { Make("banana"), Make("Apple"), Make("cherry", 1) };

        var flat = Assert.Single(TaskAgenda.Build(tasks, Today, TaskGrouping.None, TaskSort.Title, Utc));

        Assert.Equal(new[] { "Apple", "banana", "cherry" }, flat.Tasks.Select(t => t.Title));
    }

    [Fact]
    public void Area_grouping_sorts_areas_by_name_with_no_area_last()
    {
        var homework = new ProjectArea { Name = "homework" };
        var chores = new ProjectArea { Name = "Chores" };
        TaskItem InArea(string title, ProjectArea? area) =>
            new() { Title = title, AreaId = area?.Id, Area = area };

        var groups = TaskAgenda.Build(
            new[] { InArea("loose", null), InArea("essay", homework), InArea("dishes", chores) },
            Today, TaskGrouping.Area, TaskSort.Due, Utc);

        Assert.Equal(new[] { "#CHORES", "#HOMEWORK", "NO AREA" }, groups.Select(g => g.Label));
    }

    [Fact]
    public void Closed_tasks_go_last_most_recent_first()
    {
        var old = Make("old", status: TaskStatus.Completed);
        old.CompletedAt = Day(-5);
        var recent = Make("recent", status: TaskStatus.Completed);
        recent.CompletedAt = Day(-1);
        var canceled = Make("canceled", status: TaskStatus.Canceled);
        canceled.UpdatedAt = Day(-3);

        foreach (var grouping in Enum.GetValues<TaskGrouping>())
        {
            var groups = TaskAgenda.Build(new[] { old, Make("open", 0), recent, canceled },
                Today, grouping, TaskSort.Due, Utc);

            var last = groups[^1];
            Assert.Equal("COMPLETED", last.Label);
            Assert.Equal(new[] { "recent", "canceled", "old" }, last.Tasks.Select(t => t.Title));
            Assert.DoesNotContain(groups.Take(groups.Count - 1).SelectMany(g => g.Tasks), t => !t.IsOpen);
        }
    }

    [Fact]
    public void Empty_input_gives_no_sections() =>
        Assert.Empty(TaskAgenda.Build(Array.Empty<TaskItem>(), Today, TaskGrouping.Due, TaskSort.Due, Utc));
}
