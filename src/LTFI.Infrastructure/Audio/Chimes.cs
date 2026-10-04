namespace LTFI.Infrastructure.Audio;

/// <summary>LTFI's notification sounds.</summary>
public enum Chime
{
    /// <summary>A pomodoro work interval ended (rising two-note).</summary>
    WorkDone,

    /// <summary>A break ended (falling two-note).</summary>
    BreakOver,

    /// <summary>A reminders sync brought changes from the iPhone (single soft note).</summary>
    Ping,

    /// <summary>A full 10-minute NSDR completed (gentle three-note).</summary>
    NsdrDone
}

/// <summary>
/// The chime definitions and their WAV files under <c>%AppData%/LTFI/sounds</c>. Files are
/// generated on demand and regenerated if missing (delete the folder to rebuild them).
/// </summary>
public static class Chimes
{
    // Equal-tempered pitches (Hz).
    private const double G4 = 392.00;
    private const double C5 = 523.25;
    private const double E5 = 659.25;
    private const double G5 = 783.99;

    public static string FileName(Chime chime) => chime switch
    {
        Chime.WorkDone => "work-done.wav",
        Chime.BreakOver => "break-over.wav",
        Chime.Ping => "ping.wav",
        Chime.NsdrDone => "nsdr-done.wav",
        _ => throw new ArgumentOutOfRangeException(nameof(chime), chime, null)
    };

    public static IReadOnlyList<ChimeNote> Notes(Chime chime) => chime switch
    {
        Chime.WorkDone => [new(C5, 0.00, 0.55), new(G5, 0.18, 0.90)],
        Chime.BreakOver => [new(G5, 0.00, 0.55), new(C5, 0.18, 0.90)],
        Chime.Ping => [new(E5, 0.00, 0.45, 0.6)],
        Chime.NsdrDone => [new(G4, 0.00, 1.40, 0.8), new(C5, 0.45, 1.40, 0.8), new(E5, 0.90, 1.80, 0.8)],
        _ => throw new ArgumentOutOfRangeException(nameof(chime), chime, null)
    };

    public static byte[] Render(Chime chime) => ChimeSynth.RenderWav(Notes(chime));

    /// <summary>
    /// Returns the chime's WAV path in <paramref name="directory"/>, writing it first if it doesn't
    /// exist. Writes to a temp file and moves it into place so a half-written file is never played.
    /// </summary>
    public static string EnsureFile(string directory, Chime chime)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName(chime));
        if (File.Exists(path))
        {
            return path;
        }

        var temp = path + ".tmp";
        File.WriteAllBytes(temp, Render(chime));
        File.Move(temp, path, overwrite: true);
        return path;
    }

    /// <summary>Writes every missing chime file; returns the paths by chime.</summary>
    public static IReadOnlyDictionary<Chime, string> EnsureAll(string directory) =>
        Enum.GetValues<Chime>().ToDictionary(c => c, c => EnsureFile(directory, c));
}
