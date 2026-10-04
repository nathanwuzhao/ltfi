using System;
using System.IO;
using System.Threading.Tasks;
using LTFI.Infrastructure.Audio;
using LTFI.Infrastructure.Persistence;
using LTFI.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace LTFI.Services.Audio;

/// <summary>
/// Plays LTFI's chimes (generated WAVs in <c>%AppData%/LTFI/sounds</c>) on the dedicated
/// notification player, honouring <see cref="SoundSettings"/>. Never throws.
/// </summary>
public sealed class NotificationSounds
{
    private readonly IAudioPlayer _player;
    private readonly IAudioPlayer _ambient;
    private readonly SoundSettings _settings;

    public NotificationSounds(
        [FromKeyedServices(AudioPlayers.Notifications)] IAudioPlayer player,
        [FromKeyedServices(AudioPlayers.Ambient)] IAudioPlayer ambient,
        SoundSettings settings)
    {
        _player = player;
        _ambient = ambient;
        _settings = settings;

        // Write the chime files up front so the first chime doesn't wait on synthesis.
        _ = Task.Run(() =>
        {
            try
            {
                Chimes.EnsureAll(SoundsDirectory);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Couldn't write chime files to {Dir}", SoundsDirectory);
            }
        });
    }

    public static string SoundsDirectory => Path.Combine(DbPaths.AppDataDirectory, "sounds");

    /// <summary>The configured volume, clamped to 0–100.</summary>
    public int Volume => Math.Clamp(_settings.SoundVolume, 0, 100);

    public void Play(Chime chime)
    {
        if (!_settings.SoundsEnabled || Volume == 0)
        {
            return;
        }

        // MCI's waveaudio device is exclusive: a WAV chime would kill a WAV NSDR track. (An mp3/m4a
        // track plays through a different device and coexists with chimes.)
        if (_ambient.IsPlaying && IsWav(_ambient.CurrentFile))
        {
            Log.Information("Skipped {Chime} chime: a WAV track is playing", chime);
            return;
        }

        string path;
        try
        {
            path = Chimes.EnsureFile(SoundsDirectory, chime);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Couldn't prepare the {Chime} chime", chime);
            return;
        }

        _ = PlayFileAsync(path);
    }

    /// <summary>The soft "something arrived from the iPhone" ping, if sync pings are on.</summary>
    public void PlaySyncPing()
    {
        if (_settings.SyncPings)
        {
            Play(Chime.Ping);
        }
    }

    private async Task PlayFileAsync(string path)
    {
        await _player.SetVolumeAsync(Volume);
        await _player.PlayAsync(path);
    }

    private static bool IsWav(string? path) =>
        path is not null && Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase);
}
