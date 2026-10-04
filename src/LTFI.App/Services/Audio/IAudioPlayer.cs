using System;
using System.Threading.Tasks;

namespace LTFI.Services.Audio;

/// <summary>
/// Plays one audio file at a time. Implementations are best effort: no method throws (failures
/// are logged) and the returned tasks never fault, so callers may fire and forget. State flags
/// update as soon as a call is made, before the backend finishes.
/// </summary>
public interface IAudioPlayer
{
    /// <summary>A file is playing (started and not paused, stopped or finished).</summary>
    bool IsPlaying { get; }

    /// <summary>Playback is paused and can be resumed.</summary>
    bool IsPaused { get; }

    /// <summary>The file being played or paused; null when idle.</summary>
    string? CurrentFile { get; }

    /// <summary>The file played to its end (not raised by <see cref="StopAsync"/>). Raised on the UI thread.</summary>
    event EventHandler? PlaybackFinished;

    /// <summary>Stops anything playing and plays <paramref name="path"/> from the start.</summary>
    Task PlayAsync(string path);

    Task PauseAsync();

    Task ResumeAsync();

    Task StopAsync();

    /// <summary>Volume 0–100 (clamped). Best effort; on Windows it is per process, not per player.</summary>
    Task SetVolumeAsync(int percent);
}

/// <summary>DI keys for the two player singletons.</summary>
public static class AudioPlayers
{
    /// <summary>Long-form audio (the NSDR track).</summary>
    public const string Ambient = "ambient";

    /// <summary>Short notification chimes — separate so a ping never stops the NSDR track.</summary>
    public const string Notifications = "notifications";
}
