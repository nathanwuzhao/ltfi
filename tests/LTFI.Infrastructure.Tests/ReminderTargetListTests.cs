using LTFI.Core.Domain;
using Xunit;

namespace LTFI.Infrastructure.Tests;

/// <summary><see cref="ReminderRules.TargetList"/>: the list rule shared by TaskService and the Tasks editor.</summary>
public class ReminderTargetListTests
{
    [Fact]
    public void Standing_project_goes_to_the_area_list() =>
        Assert.Equal("job", ReminderRules.TargetList(true, "job", "LTFI"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Standing_project_without_area_has_no_list(string? area) =>
        Assert.Null(ReminderRules.TargetList(true, area, "LTFI"));

    [Fact]
    public void Other_projects_go_to_the_configured_ltfi_list_and_ignore_the_area() =>
        Assert.Equal("Work", ReminderRules.TargetList(false, "job", " Work "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_setting_falls_back_to_LTFI(string? configured) =>
        Assert.Equal("LTFI", ReminderRules.TargetList(false, null, configured));
}
