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
    /// A project's colour: <see cref="StandingColor"/> for a standing project, otherwise a palette
    /// entry picked by a fixed hash (FNV-1a) of its id — stable across runs and machines.
    /// </summary>
    public static string ColorFor(Guid projectId, bool isStanding = false) =>
        isStanding ? StandingColor : Palette[PaletteIndex(projectId)];

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
