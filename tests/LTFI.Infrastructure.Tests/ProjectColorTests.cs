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

namespace LTFI.Infrastructure.Tests;

/// <summary>
/// A project's picked colour (Projects editor) overrides the automatic one, is stored normalized,
/// and survives a save; clearing it returns to automatic.
/// </summary>
public sealed class ProjectColorTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"ltfi-pc-{Guid.NewGuid():N}");
    private readonly TestDbFactory _factory;

    public ProjectColorTests()
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

    [Theory]
    [InlineData("#e39a7b", "#E39A7B")]
    [InlineData(" A3C47E ", "#A3C47E")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("#123", null)]
    [InlineData("#GGGGGG", null)]
    public void NormalizeColor_AcceptsOnlySixDigitHex(string? input, string? expected) =>
        Assert.Equal(expected, ProjectCodes.NormalizeColor(input));

    [Fact]
    public void PickedColour_OverridesAutomaticAndStanding()
    {
        var id = Guid.NewGuid();
        Assert.Equal("#E39A7B", ProjectCodes.ColorFor(id, isStanding: false, custom: "#e39a7b"));
        Assert.Equal("#E39A7B", ProjectCodes.ColorFor(id, isStanding: true, custom: "#E39A7B"));
        Assert.Equal(ProjectCodes.StandingColor, ProjectCodes.ColorFor(id, isStanding: true));
        Assert.Equal(ProjectCodes.Palette[ProjectCodes.PaletteIndex(id)], ProjectCodes.ColorFor(id, custom: "nope"));
        Assert.Equal(ProjectCodes.NoneColor, ProjectCodes.ColorOrNone(null, custom: "#E39A7B"));
    }

    [Fact]
    public void Swatches_AreDistinctAndIncludeThePalette()
    {
        Assert.Equal(ProjectCodes.Swatches.Count, ProjectCodes.Swatches.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ProjectCodes.Palette, c => Assert.Contains(c, ProjectCodes.Swatches));
        Assert.All(ProjectCodes.Swatches, c => Assert.Equal(c.ToUpperInvariant(), ProjectCodes.NormalizeColor(c)));
    }

    [Fact]
    public async Task Colour_IsSavedOnCreateAndUpdate_AndClearsToAutomatic()
    {
        var service = new ProjectService(_factory);
        var created = await service.CreateAsync(new ProjectDraft { Title = "boids", Color = "#a3c47e" });
        Assert.Equal("#A3C47E", (await service.GetByIdAsync(created.Id))!.Color);

        await service.UpdateAsync(created.Id, new ProjectDraft { Title = "boids", Color = "#E58FD0" });
        Assert.Equal("#E58FD0", (await service.GetByIdAsync(created.Id))!.Color);

        await service.UpdateAsync(created.Id, new ProjectDraft { Title = "boids", Color = null });
        Assert.Null((await service.GetByIdAsync(created.Id))!.Color);
    }
}
