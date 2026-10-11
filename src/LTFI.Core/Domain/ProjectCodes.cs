using System;
using System.Collections.Generic;
using System.Linq;

namespace LTFI.Core.Domain;

/// <summary>
/// Short project codes and their stable colours for compact UI (the Command Center evidence feed,
/// project lists, upcoming targets). Pure, so the same project always reads the same everywhere.
/// </summary>
public static class ProjectCodes
{
    /// <summary>Shown when there is no project (or the title has no letters/digits).</summary>
    public const string None = "—";

    /// <summary>
    /// Muted categorical colours that read on the near-black background and stay clear of the
    /// status green/amber/red. Hex <c>#RRGGBB</c>.
    /// </summary>
    public static IReadOnlyList<string> Palette { get; } =
    [
        "#7FA7D9", // steel blue
        "#A98BD8", // lavender
        "#5DB7B3", // teal
        "#D58BA6", // rose
        "#C4AE8C", // sand
        "#8E95E0", // periwinkle
        "#6FBCD6", // sky
        "#B79BC0"  // mauve
    ];

    /// <summary>Standing projects (e.g. "Life") get a neutral, dim colour.</summary>
    public const string StandingColor = "#7A828F";

    /// <summary>
    /// The first 4 letters/digits of the title, uppercased ("boids 01" → "BOID", "Life" → "LIFE");
    /// shorter titles as they are; <see cref="None"/> for none.
    /// </summary>
    public static string Code(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return None;
        }

        var raw = new string(title.Where(char.IsLetterOrDigit).Take(4).ToArray());
        return raw.Length == 0 ? None : raw.ToUpperInvariant();
    }

    /// <summary>
    /// The colours offered in the Projects editor's picker: the automatic palette plus a few more,
    /// all far enough apart to tell projects apart at a glance.
    /// </summary>
    public static IReadOnlyList<string> Swatches { get; } =
    [
        .. Palette,
        "#E39A7B", // coral
        "#A3C47E", // moss
        "#E2C46F", // gold
        "#E58FD0", // orchid
        StandingColor
    ];

    /// <summary>
    /// A picked colour as stored: <c>#RRGGBB</c> uppercased, or null (automatic) for empty or
    /// anything that isn't a 6-digit hex colour.
    /// </summary>
    public static string? NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return null;
        }

        var hex = color.Trim().TrimStart('#');
        return hex.Length == 6 && hex.All(Uri.IsHexDigit) ? "#" + hex.ToUpperInvariant() : null;
    }

    /// <summary>
    /// A project's colour: the picked <paramref name="custom"/> colour when there is one; otherwise
    /// <see cref="StandingColor"/> for a standing project, else a palette entry picked by a fixed
    /// hash (FNV-1a) of its id — stable across runs and machines.
    /// </summary>
    public static string ColorFor(Guid projectId, bool isStanding = false, string? custom = null) =>
        NormalizeColor(custom) ?? (isStanding ? StandingColor : Palette[PaletteIndex(projectId)]);

    /// <summary>No project at all (e.g. "(No project)", evidence without one): the faint text colour.</summary>
    public const string NoneColor = "#5B636F";

    /// <summary>
    /// <see cref="ColorFor(Guid, bool, string?)"/> for an optional project: <see cref="NoneColor"/> when there is none.
    /// The single rule every screen's project colour goes through.
    /// </summary>
    public static string ColorOrNone(Guid? projectId, bool isStanding = false, string? custom = null) =>
        projectId is { } id ? ColorFor(id, isStanding, custom) : NoneColor;

    /// <summary>The palette slot for <paramref name="projectId"/>.</summary>
    public static int PaletteIndex(Guid projectId)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var b in projectId.ToByteArray())
        {
            hash = (hash ^ b) * prime;
        }

        return (int)(hash % (uint)Palette.Count);
    }
}
