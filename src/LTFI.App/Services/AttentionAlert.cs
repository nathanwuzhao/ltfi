using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using LTFI.Infrastructure.Audio;
using LTFI.Services.Audio;
using Serilog;

namespace LTFI.Services;

/// <summary>
/// Gets the user's attention when a pomodoro phase or NSDR ends: an LTFI chime (respecting the
/// sound settings) and the main window brought forward. Best effort — failures are logged, never thrown.
/// </summary>
public sealed class AttentionAlert(NotificationSounds sounds)
{
    public void Raise(Chime chime)
    {
        try
        {
            sounds.Play(chime);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Attention sound failed");
        }

        Dispatcher.UIThread.Post(BringToFront);
    }

    private static void BringToFront()
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            {
                return;
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            // Toggling Topmost pops the window above others even when Activate() is refused
            // by the OS foreground-lock rules (the taskbar button flashes instead).
            window.Topmost = true;
            window.Activate();
            window.Topmost = false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Bringing the window to attention failed");
        }
    }
}
