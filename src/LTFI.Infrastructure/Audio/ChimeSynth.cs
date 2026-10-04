using System.Text;

namespace LTFI.Infrastructure.Audio;

/// <summary>One tone in a chime: frequency, when it starts, how long it rings, and its relative loudness.</summary>
public sealed record ChimeNote(double FrequencyHz, double StartSeconds, double DurationSeconds, double Gain = 1.0);

/// <summary>
/// Pure WAV synthesis for LTFI's notification chimes: sine tones (plus a quiet octave partial for
/// a bell-like colour) shaped by a short linear attack and an exponential decay, mixed, normalised
/// and encoded as a 44.1 kHz 16-bit mono PCM WAV. No I/O — returns bytes.
/// </summary>
public static class ChimeSynth
{
    public const int SampleRate = 44_100;
    public const short BitsPerSample = 16;
    public const short Channels = 1;

    /// <summary>Peak level of the normalised mix (0..1 of full scale) — soft, never clipping.</summary>
    public const double PeakLevel = 0.45;

    private const double AttackSeconds = 0.012;
    private const double ReleaseSeconds = 0.02;
    private const double TailSeconds = 0.05;

    /// <summary>Renders <paramref name="notes"/> to a complete WAV file (header + PCM data).</summary>
    public static byte[] RenderWav(IReadOnlyList<ChimeNote> notes, int sampleRate = SampleRate)
        => EncodeWav(RenderSamples(notes, sampleRate), sampleRate);

    /// <summary>Mixes the notes into 16-bit samples. Total length = last note end + a short tail.</summary>
    public static short[] RenderSamples(IReadOnlyList<ChimeNote> notes, int sampleRate = SampleRate)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);

        var end = notes.Count == 0 ? 0 : notes.Max(n => n.StartSeconds + n.DurationSeconds);
        var total = (int)Math.Ceiling((end + (notes.Count == 0 ? 0 : TailSeconds)) * sampleRate);
        var mix = new double[total];

        foreach (var note in notes)
        {
            if (note.DurationSeconds <= 0 || note.FrequencyHz <= 0)
            {
                continue;
            }

            var start = (int)Math.Round(note.StartSeconds * sampleRate);
            var length = (int)Math.Round(note.DurationSeconds * sampleRate);
            // Decay so the tone falls to ~1% (-40 dB) by its end; the release ramp removes the click.
            var decayRate = Math.Log(100) / note.DurationSeconds;

            for (var i = 0; i < length && start + i < total; i++)
            {
                var t = (double)i / sampleRate;
                var envelope = Envelope(t, note.DurationSeconds, decayRate);
                var phase = 2 * Math.PI * note.FrequencyHz * t;
                var tone = Math.Sin(phase) + 0.18 * Math.Sin(2 * phase) * Math.Exp(-t * 6);
                mix[start + i] += note.Gain * envelope * tone;
            }
        }

        var peak = mix.Length == 0 ? 0 : mix.Max(Math.Abs);
        var scale = peak > 0 ? PeakLevel * short.MaxValue / peak : 0;
        var samples = new short[total];
        for (var i = 0; i < total; i++)
        {
            samples[i] = (short)Math.Clamp(Math.Round(mix[i] * scale), short.MinValue, short.MaxValue);
        }

        return samples;
    }

    /// <summary>Linear attack, exponential decay, short linear release to exactly zero.</summary>
    internal static double Envelope(double t, double duration, double decayRate)
    {
        if (t < 0 || t >= duration)
        {
            return 0;
        }

        var attack = t < AttackSeconds ? t / AttackSeconds : 1.0;
        var decay = Math.Exp(-decayRate * t);
        var release = Math.Min(1.0, (duration - t) / ReleaseSeconds);
        return attack * decay * release;
    }

    /// <summary>Wraps 16-bit mono PCM samples in a canonical 44-byte RIFF/WAVE header.</summary>
    public static byte[] EncodeWav(short[] samples, int sampleRate = SampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var blockAlign = (short)(Channels * BitsPerSample / 8);
        var dataBytes = samples.Length * blockAlign;

        using var stream = new MemoryStream(44 + dataBytes);
        using (var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8);
            w.Write(36 + dataBytes);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);                          // PCM fmt chunk size
            w.Write((short)1);                    // PCM
            w.Write(Channels);
            w.Write(sampleRate);
            w.Write(sampleRate * blockAlign);     // byte rate
            w.Write(blockAlign);
            w.Write(BitsPerSample);
            w.Write("data"u8);
            w.Write(dataBytes);
            foreach (var s in samples)
            {
                w.Write(s);                       // little-endian
            }
        }

        return stream.ToArray();
    }
}
