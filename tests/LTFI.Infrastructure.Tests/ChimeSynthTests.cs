using System.Buffers.Binary;
using System.Text;
using LTFI.Infrastructure.Audio;
using LTFI.Infrastructure.Settings;

namespace LTFI.Infrastructure.Tests;

public class ChimeSynthTests
{
    [Fact]
    public void EncodeWav_writes_a_canonical_pcm_header()
    {
        short[] samples = [0, 1000, -1000, short.MaxValue, short.MinValue];
        var wav = ChimeSynth.EncodeWav(samples);

        Assert.Equal(44 + samples.Length * 2, wav.Length);
        Assert.Equal("RIFF", Ascii(wav, 0));
        Assert.Equal(wav.Length - 8, I32(wav, 4));
        Assert.Equal("WAVE", Ascii(wav, 8));
        Assert.Equal("fmt ", Ascii(wav, 12));
        Assert.Equal(16, I32(wav, 16));
        Assert.Equal(1, I16(wav, 20));           // PCM
        Assert.Equal(1, I16(wav, 22));           // mono
        Assert.Equal(44_100, I32(wav, 24));
        Assert.Equal(44_100 * 2, I32(wav, 28));  // byte rate
        Assert.Equal(2, I16(wav, 32));           // block align
        Assert.Equal(16, I16(wav, 34));          // bits
        Assert.Equal("data", Ascii(wav, 36));
        Assert.Equal(samples.Length * 2, I32(wav, 40));

        for (var i = 0; i < samples.Length; i++)
        {
            Assert.Equal(samples[i], I16(wav, 44 + i * 2));
        }
    }

    [Fact]
    public void RenderSamples_length_covers_the_last_note_plus_a_short_tail()
    {
        var samples = ChimeSynth.RenderSamples([new ChimeNote(440, 0.2, 0.5)]);
        var seconds = samples.Length / (double)ChimeSynth.SampleRate;
        Assert.InRange(seconds, 0.7, 0.8);
    }

    [Fact]
    public void RenderSamples_is_normalised_soft_and_starts_and_ends_silent()
    {
        var samples = ChimeSynth.RenderSamples(Chimes.Notes(Chime.WorkDone));
        var peak = samples.Max(s => Math.Abs((int)s));

        Assert.InRange(peak, (int)(0.44 * short.MaxValue), (int)(0.46 * short.MaxValue));
        Assert.Equal(0, samples[0]);                                     // attack starts at zero
        Assert.All(samples[^100..], s => Assert.Equal(0, s));            // tail is silent (no click)
        Assert.True(samples.Take(200).Max(s => Math.Abs((int)s)) < peak / 2); // ramps in, no click
    }

    [Fact]
    public void A_single_tone_has_the_requested_pitch()
    {
        // Count zero crossings over the steady part of a 1 s, 440 Hz note (~880 crossings/s).
        var samples = ChimeSynth.RenderSamples([new ChimeNote(440, 0, 1.0)]);
        var window = samples[(ChimeSynth.SampleRate / 10)..(ChimeSynth.SampleRate / 2)];
        var crossings = 0;
        for (var i = 1; i < window.Length; i++)
        {
            if ((window[i - 1] < 0) != (window[i] < 0))
            {
                crossings++;
            }
        }

        var hz = crossings / 2.0 / (window.Length / (double)ChimeSynth.SampleRate);
        Assert.InRange(hz, 430, 450);
    }

    [Fact]
    public void Empty_note_list_renders_an_empty_but_valid_wav()
    {
        var wav = ChimeSynth.RenderWav([]);
        Assert.Equal(44, wav.Length);
        Assert.Equal(0, I32(wav, 40));
    }

    [Theory]
    [InlineData(Chime.WorkDone, "work-done.wav")]
    [InlineData(Chime.BreakOver, "break-over.wav")]
    [InlineData(Chime.Ping, "ping.wav")]
    [InlineData(Chime.NsdrDone, "nsdr-done.wav")]
    public void Every_chime_renders_a_short_wav(Chime chime, string fileName)
    {
        Assert.Equal(fileName, Chimes.FileName(chime));
        var wav = Chimes.Render(chime);
        var seconds = (wav.Length - 44) / 2.0 / ChimeSynth.SampleRate;
        Assert.InRange(seconds, 0.3, 3.0);
    }

    [Fact]
    public void Two_note_chimes_rise_and_fall()
    {
        var work = Chimes.Notes(Chime.WorkDone);
        var brk = Chimes.Notes(Chime.BreakOver);
        Assert.True(work[1].FrequencyHz > work[0].FrequencyHz);
        Assert.True(brk[1].FrequencyHz < brk[0].FrequencyHz);
        Assert.Single(Chimes.Notes(Chime.Ping));
        Assert.Equal(3, Chimes.Notes(Chime.NsdrDone).Count);
    }

    [Fact]
    public void EnsureFile_writes_once_and_regenerates_when_missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ltfi-chimes-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = Chimes.EnsureAll(dir);
            Assert.Equal(4, paths.Count);
            Assert.All(paths.Values, p => Assert.True(File.Exists(p)));

            // An existing file is left alone.
            var ping = paths[Chime.Ping];
            File.WriteAllBytes(ping, [1, 2, 3]);
            Assert.Equal(ping, Chimes.EnsureFile(dir, Chime.Ping));
            Assert.Equal(3, new FileInfo(ping).Length);

            // A missing one is regenerated.
            File.Delete(ping);
            Chimes.EnsureFile(dir, Chime.Ping);
            Assert.Equal(Chimes.Render(Chime.Ping), File.ReadAllBytes(ping));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Sound_settings_default_on_and_read_camelCase_json()
    {
        var path = Path.Combine(Path.GetTempPath(), "ltfi-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var defaults = SettingsStore.Load(path); // writes defaults
            Assert.True(defaults.Sounds.SoundsEnabled);
            Assert.Equal(60, defaults.Sounds.SoundVolume);
            Assert.True(defaults.Sounds.SyncPings);
            Assert.Contains("\"soundVolume\": 60", File.ReadAllText(path));

            File.WriteAllText(path, """{ "sounds": { "soundsEnabled": false, "soundVolume": 25, "syncPings": false } }""");
            var loaded = SettingsStore.Load(path);
            Assert.False(loaded.Sounds.SoundsEnabled);
            Assert.Equal(25, loaded.Sounds.SoundVolume);
            Assert.False(loaded.Sounds.SyncPings);

            // Older files without a "sounds" section get the defaults.
            File.WriteAllText(path, """{ "focus": { "nsdrAudioUrl": null } }""");
            Assert.True(SettingsStore.Load(path).Sounds.SoundsEnabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Ascii(byte[] b, int offset) => Encoding.ASCII.GetString(b, offset, 4);

    private static int I32(byte[] b, int offset) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(offset));

    private static short I16(byte[] b, int offset) => BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(offset));
}
