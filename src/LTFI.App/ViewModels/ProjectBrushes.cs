using System;
using System.Collections.Generic;
using Avalonia.Data.Converters;
using Avalonia.Media;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;

namespace LTFI.ViewModels;

/// <summary>
/// The app's one source of project identity colour and code: frozen brushes for
/// <see cref="ProjectCodes"/> colours (cached per hex) plus XAML converters. Project colour is for
/// identity only; status keeps green/amber/red (<see cref="CcBrush"/>). Standing projects
/// ("Life") are the neutral grey, no project is the faint text colour.
/// </summary>
public static class ProjectBrushes
{
    private static readonly Dictionary<string, IBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    // Colours picked in the Projects editor, by project id — so screens that only know an id
    // (evidence feed, review lines, task options) still show the picked colour.
    private static readonly Dictionary<Guid, string> Picked = new();

    /// <summary>
    /// Records each project's picked colour (or clears it when back on automatic). Every page that
    /// loads the project list calls this, so id-only lookups stay in step.
    /// </summary>
    public static void Remember(IEnumerable<Project> projects)
    {
        lock (Picked)
        {
            foreach (var p in projects)
            {
                Remember(p);
            }
        }
    }

    private static void Remember(Project p)
    {
        lock (Picked)
        {
            if (ProjectCodes.NormalizeColor(p.Color) is { } hex)
            {
                Picked[p.Id] = hex;
            }
            else
            {
                Picked.Remove(p.Id);
            }
        }
    }

    private static string? PickedFor(Guid? projectId)
    {
        lock (Picked)
        {
            return projectId is { } id && Picked.TryGetValue(id, out var hex) ? hex : null;
        }
    }

    /// <summary>A frozen brush for a <c>#RRGGBB</c> colour, cached.</summary>
    public static IBrush For(string hex)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(hex, out var brush))
            {
                brush = new SolidColorBrush(Color.Parse(hex)).ToImmutable();
                Cache[hex] = brush;
            }

            return brush;
        }
    }

    /// <summary>The project's colour by id: picked, else automatic (standing = neutral grey); none = faint.</summary>
    public static IBrush For(Guid? projectId, bool isStanding = false) =>
        For(ProjectCodes.ColorOrNone(projectId, isStanding, PickedFor(projectId)));

    /// <summary>The project's colour; faint for no project.</summary>
    public static IBrush For(Project? project)
    {
        if (project is null)
        {
            return For((Guid?)null);
        }

        Remember(project);
        return For(ProjectCodes.ColorFor(project.Id, project.IsStanding, project.Color));
    }

    /// <summary>
    /// Anything that names a project → its brush: <see cref="Project"/>, <see cref="ProjectOption"/>,
    /// <see cref="ProjectActivityLine"/>, <see cref="StalledProjectLine"/>, a <see cref="Guid"/>; else faint.
    /// </summary>
    public static IBrush ForAny(object? value) => value switch
    {
        Project p => For(p),
        ProjectOption o => For(o.Id, o.IsStanding),
        ProjectActivityLine a => For(a.ProjectId, a.IsStanding),
        StalledProjectLine s => For(s.ProjectId),
        Guid id => For(id),
        _ => For((Guid?)null)
    };

    /// <summary>XAML: <c>Foreground="{Binding Project, Converter={x:Static vm:ProjectBrushes.Brush}}"</c>.</summary>
    public static readonly IValueConverter Brush = new FuncValueConverter<object?, IBrush>(ForAny);

    /// <summary>XAML: a <see cref="Project"/>/<see cref="ProjectOption"/>/title → its 4-char code ("BOID").</summary>
    public static readonly IValueConverter Code = new FuncValueConverter<object?, string>(v => v switch
    {
        Project p => ProjectCodes.Code(p.Title),
        ProjectOption { Id: not null } o => ProjectCodes.Code(o.Name),
        string title => ProjectCodes.Code(title),
        _ => ProjectCodes.None
    });
}
