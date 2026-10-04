using System;
using System.Collections.Generic;

namespace LTFI.Core.Domain;

/// <summary>One step of the NSDR guide, shown from <see cref="At"/> (elapsed) until the next cue.</summary>
public sealed record NsdrCue(TimeSpan At, string Title, string Text);

/// <summary>
/// A 10-minute non-sleep deep rest (NSDR): lie still, breathe slowly, scan the body. The guide is
/// plain data — timed cues the UI advances through — so it can be tested and reworded in one place.
/// </summary>
public static class Nsdr
{
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(10);

    public static IReadOnlyList<NsdrCue> Cues { get; } =
    [
        new(TimeSpan.Zero, "Settle",
            "Lie down or sit back. Close your eyes. Breathe in through the nose, then let a long, slow exhale out — longer than the inhale."),
        new(TimeSpan.FromSeconds(90), "Feet and legs",
            "Bring attention to your feet. Notice weight, temperature, contact. Move slowly up through the calves, knees and thighs, letting each part go heavy."),
        new(TimeSpan.FromSeconds(210), "Torso, hands, arms",
            "Notice the hips, belly and chest rising and falling. Then the hands — fingers, palms — and up the arms to the shoulders. Let them rest."),
        new(TimeSpan.FromSeconds(330), "Face",
            "Unclench the jaw. Let the tongue rest. Soften the eyes, the forehead, the small muscles around the mouth."),
        new(TimeSpan.FromSeconds(420), "Whole body",
            "Feel the whole body at once, lying still. Breathe slowly; nothing to do. If the mind wanders, come back to the exhale."),
        new(TimeSpan.FromSeconds(540), "Return",
            "Begin to deepen the breath. Move fingers and toes. When ready, open your eyes and sit up slowly.")
    ];

    /// <summary>Index of the cue active at <paramref name="elapsed"/> (clamped to the first/last cue).</summary>
    public static int CueIndexAt(TimeSpan elapsed)
    {
        var index = 0;
        for (var i = 0; i < Cues.Count; i++)
        {
            if (elapsed >= Cues[i].At)
            {
                index = i;
            }
        }

        return index;
    }

    /// <summary>The cue active at <paramref name="elapsed"/>.</summary>
    public static NsdrCue CueAt(TimeSpan elapsed) => Cues[CueIndexAt(elapsed)];
}
