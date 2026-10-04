using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using NetCoreAudio;
using Serilog;

namespace LTFI.Services.Audio;

/// <summary>
/// <see cref="IAudioPlayer"/> over NetCoreAudio (MCI on Windows, afplay/aplay elsewhere). Backend
/// calls are serialised and run off the UI thread (opening an mp3 through MCI blocks for a few
/// hundred ms). Every failure is logged and swallowed. NetCoreAudio types stay in this file.
/// </summary>
/// <remarks>
/// Windows notes (found by testing NetCoreAudio 2.0.1): <c>Play</c> while playing does not stop
/// the previous file or reset its end timer, so <see cref="PlayAsync"/> stops first; <c>Stop</c>
/// does not raise PlaybackFinished; pause time is accounted for in the end timer. Two WAV files on
/// two players can't play at once (MCI waveaudio is exclusive); an mp3 track plus a WAV chime can.
/// </remarks>
public sealed class NetCoreAudioPlayer : IAudioPlayer, IDisposable
{
    private readonly string _name;
    private readonly Player? _player;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile bool _playing;
    private volatile bool _paused;
    private volatile string? _current;

    public NetCoreAudioPlayer(string name)
    {
        _name = name;
        try
        {
            _player = new Player();
            _player.PlaybackFinished += OnBackendFinished;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Audio player {Player} unavailable; sounds disabled", name);
        }
    }

    public bool IsPlaying => _playing && !_paused;

    public bool IsPaused => _playing && _paused;

    public string? CurrentFile => _current;

    public event EventHandler? PlaybackFinished;

    public Task PlayAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Log.Warning("Audio {Player}: file not found {Path}", _name, path);
            return Task.CompletedTask;
        }

        var wasPlaying = _playing;
        _current = path;
        _playing = true;
        _paused = false;

        return RunAsync("play", async player =>
        {
            if (wasPlaying || player.Playing)
            {
                await player.Stop();
            }

            await player.Play(path);
        }, onFailure: () => Reset(path));
    }

    public Task PauseAsync()
    {
        if (!_playing || _paused)
        {
            return Task.CompletedTask;
        }

        _paused = true;
        return RunAsync("pause", player => player.Pause(), onFailure: () => _paused = false);
    }

    public Task ResumeAsync()
    {
        if (!_playing || !_paused)
        {
            return Task.CompletedTask;
        }

        _paused = false;
        return RunAsync("resume", player => player.Resume(), onFailure: () => Reset(_current));
    }

    public Task StopAsync()
    {
        if (!_playing)
        {
            return Task.CompletedTask;
        }

        Reset(_current);
        return RunAsync("stop", player => player.Playing ? player.Stop() : Task.CompletedTask);
    }

    public Task SetVolumeAsync(int percent)
    {
        var volume = (byte)Math.Clamp(percent, 0, 100);
        return RunAsync("volume", player => player.SetVolume(volume));
    }

    public void Dispose()
    {
        try
        {
            if (_player is { Playing: true })
            {
                _player.Stop().Wait(TimeSpan.FromMilliseconds(500));
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Audio {Player}: stop on dispose failed", _name);
        }
    }

    /// <summary>Clears the playing state, but only if <paramref name="path"/> is still the current file.</summary>
    private void Reset(string? path)
    {
        if (path is not null && !string.Equals(_current, path, StringComparison.Ordinal))
        {
            return;
        }

        _playing = false;
        _paused = false;
        _current = null;
    }

    private async Task RunAsync(string operation, Func<Player, Task> action, Action? onFailure = null)
    {
        if (_player is null)
        {
            onFailure?.Invoke();
            return;
        }

        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await Task.Run(() => action(_player)).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            onFailure?.Invoke();
            Log.Warning(ex, "Audio {Player}: {Operation} failed ({File})", _name, operation, _current);
        }
    }

    private void OnBackendFinished(object? sender, EventArgs e)
    {
        Reset(null);
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    PlaybackFinished?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Audio {Player}: PlaybackFinished handler failed", _name);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Audio {Player}: couldn't dispatch PlaybackFinished", _name);
        }
    }
}
